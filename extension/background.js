// DownloadManager Integration — background service worker.
// Owns the native-messaging connection, download interception, the per-tab media list and context menus.

const HOST = "com.downloadmanager.host";
const REQUEST_TIMEOUT_MS = 25000;
const RETRY_IDLE_MS = 30000;
const MAX_MEDIA_PER_TAB = 60;
const RESOLVE_TIMEOUT_MS = 120000; // yt-dlp may need tens of seconds for some sites

// Sites whose players use protected or adaptive URLs that only yt-dlp can turn into a file.
const YTDLP_SITES = [
  "youtube.com", "youtu.be", "vimeo.com", "dailymotion.com", "twitch.tv", "twitter.com", "x.com",
  "facebook.com", "instagram.com", "tiktok.com", "reddit.com", "soundcloud.com", "bilibili.com",
];
// Sniffed requests from these hosts are fragments of adaptive streams, not downloadable files.
const FRAGMENT_HOSTS = ["googlevideo.com", "fbcdn.net", "cdninstagram.com", "tiktokcdn.com", "vimeocdn.com"];

const state = {
  port: null,
  connected: false,
  connecting: null, // Promise while a connection attempt is in flight
  config: {
    enabled: true,
    catchExtensions: [],
    excludeSites: [],
    minSizeBytes: 0,
    showVideoButton: true,
  },
  userEnabled: true, // toggle from the popup, stored locally
  pending: new Map(), // requestId -> { resolve, timer }
  media: new Map(), // tabId -> Map(key -> item)
  pages: new Map(), // tabId -> page address without query or hash
  bypassUntil: 0,
  retryTimer: null,
  problem: null, // why the last connection attempt failed, in words a user can act on
};

/** Turns Chrome's native-messaging errors and the host's own error codes into an actionable message. */
function describeFailure(raw) {
  const text = String(raw || "");
  if (/forbidden/i.test(text))
    return `This copy of the extension (ID ${chrome.runtime.id}) is not the one DownloadManager trusts. ` +
      `Remove it and load the "extension" folder from DownloadManager's install folder instead ` +
      `(DownloadManager tray icon → Setup… → Open extension folder).`;
  if (/not found/i.test(text))
    return "DownloadManager is not installed or has not been opened yet. Install it, open it once, then try again.";
  if (text === "app_not_running") return "DownloadManager is not running.";
  if (text === "app_unavailable") return "DownloadManager could not be started. Open it from the Start menu and try again.";
  if (text === "timeout") return "DownloadManager did not answer in time. Open it from the Start menu and try again.";
  if (/exited/i.test(text)) return "DownloadManager's browser helper stopped unexpectedly. Reinstalling DownloadManager usually fixes this.";
  return text || "DownloadManager could not be reached.";
}

// ---------------------------------------------------------------- connection

function connect(launch) {
  if (state.connected) return Promise.resolve(true);
  if (state.connecting) return state.connecting;
  state.connecting = new Promise((resolve) => {
    let port;
    try {
      port = chrome.runtime.connectNative(HOST);
    } catch (e) {
      state.problem = describeFailure(e?.message);
      resolve(false);
      state.connecting = null;
      return;
    }
    state.port = port;
    const timer = setTimeout(() => {
      state.problem = describeFailure("timeout");
      port.disconnect();
      finish(false);
    }, launch ? REQUEST_TIMEOUT_MS : 5000);

    function finish(ok) {
      clearTimeout(timer);
      state.connecting = null;
      if (!ok) {
        if (state.port === port) state.port = null;
        scheduleRetry();
      }
      resolve(ok);
    }

    port.onMessage.addListener((msg) => {
      if (msg.type === "hello") {
        state.connected = true;
        state.problem = null;
        if (msg.config) state.config = { ...state.config, ...msg.config };
        updateAllBadges();
        finish(true);
      } else if (msg.type === "error") {
        state.problem = describeFailure(msg.error);
        port.disconnect();
        finish(false);
      } else {
        onHostMessage(msg);
      }
    });
    port.onDisconnect.addListener(() => {
      const reason = chrome.runtime.lastError?.message;
      if (reason) state.problem = describeFailure(reason);
      if (state.port === port) {
        state.port = null;
        state.connected = false;
        for (const [, p] of state.pending) {
          clearTimeout(p.timer);
          p.resolve({ accepted: false, error: "disconnected" });
        }
        state.pending.clear();
        updateAllBadges();
      }
      finish(false);
    });
    port.postMessage({
      type: "hello",
      browser: navigator.userAgent,
      version: chrome.runtime.getManifest().version,
      launch,
    });
  });
  return state.connecting;
}

function scheduleRetry() {
  if (state.retryTimer || state.connected) return;
  state.retryTimer = setTimeout(() => {
    state.retryTimer = null;
    connect(false);
  }, RETRY_IDLE_MS);
}

function onHostMessage(msg) {
  if ((msg.type === "result" || msg.type === "formats") && msg.requestId && state.pending.has(msg.requestId)) {
    const p = state.pending.get(msg.requestId);
    state.pending.delete(msg.requestId);
    clearTimeout(p.timer);
    p.resolve(msg);
  } else if (msg.type === "config" && msg.config) {
    state.config = { ...state.config, ...msg.config };
  }
}

/** Sends a request to the app and resolves with its "result". `launch` starts the app if needed. */
async function request(message, launch, timeoutMs = REQUEST_TIMEOUT_MS) {
  if (!(await connect(launch))) return { accepted: false, error: state.problem || describeFailure("app_unavailable") };
  const requestId = crypto.randomUUID();
  return new Promise((resolve) => {
    const timer = setTimeout(() => {
      state.pending.delete(requestId);
      resolve({ accepted: false, error: "The app did not answer in time" });
    }, timeoutMs);
    state.pending.set(requestId, { resolve, timer });
    state.port.postMessage({ ...message, requestId });
  });
}

function interceptionActive() {
  return state.connected && state.config.enabled && state.userEnabled;
}

// ---------------------------------------------------------------- helpers

function extOf(nameOrPath) {
  const m = /\.([a-z0-9]{1,8})$/i.exec((nameOrPath || "").split(/[?#]/)[0]);
  return m ? m[1].toLowerCase() : "";
}

function baseName(path) {
  return (path || "").split(/[\\/]/).pop();
}

function siteExcluded(url) {
  let host;
  try {
    host = new URL(url).hostname;
  } catch {
    return true;
  }
  return state.config.excludeSites.some((s) => host === s || host.endsWith("." + s));
}

async function cookieHeader(url) {
  try {
    const cookies = await chrome.cookies.getAll({ url });
    return cookies.map((c) => `${c.name}=${c.value}`).join("; ") || null;
  } catch {
    return null;
  }
}

/** Cookies with their scope, for yt-dlp (which needs a cookies file rather than a header). */
async function cookieJar(url) {
  try {
    const cookies = await chrome.cookies.getAll({ url });
    return cookies.map((c) => ({
      name: c.name,
      value: c.value,
      domain: c.domain,
      path: c.path,
      secure: c.secure,
      hostOnly: c.hostOnly,
      expirationDate: c.expirationDate ?? null,
    }));
  } catch {
    return null;
  }
}

function hostMatches(url, hosts) {
  try {
    const host = new URL(url).hostname;
    return hosts.some((h) => host === h || host.endsWith("." + h));
  } catch {
    return false;
  }
}

function header(headers, name) {
  const h = headers?.find((x) => x.name.toLowerCase() === name);
  return h ? h.value : null;
}

// ---------------------------------------------------------------- request details

// What the browser really sent, so the app can repeat a request that needs more than cookies.
const MAX_TRACKED_REQUESTS = 200;
const MAX_REPLAY_BODY_BYTES = 1024 * 1024;
const NOT_FORWARDED = /^(cookie|host|content-length|connection|range|accept-encoding|user-agent|referer|upgrade-insecure-requests|if-.*|sec-.*)$/i;
const TRACKED_TYPES = ["main_frame", "sub_frame", "media", "xmlhttprequest", "object", "other"];
const tracked = new Map(); // url -> { method, body, replayable, headers, referrer }

function track(url) {
  let entry = tracked.get(url);
  if (entry) tracked.delete(url); // re-insert as most recent
  entry ??= {};
  tracked.set(url, entry);
  while (tracked.size > MAX_TRACKED_REQUESTS) tracked.delete(tracked.keys().next().value);
  return entry;
}

function toBase64(bytes) {
  let text = "";
  for (let i = 0; i < bytes.length; i += 0x8000) text += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(text);
}

/** The POST body as base64, or null when it cannot be repeated (files, or too large). */
function encodeBody(requestBody) {
  if (requestBody?.formData) {
    const form = new URLSearchParams();
    for (const [name, values] of Object.entries(requestBody.formData)) for (const v of values) form.append(name, v);
    return toBase64(new TextEncoder().encode(form.toString()));
  }
  const raw = requestBody?.raw;
  if (!raw?.length || raw.some((part) => !part.bytes)) return null;
  const size = raw.reduce((n, part) => n + part.bytes.byteLength, 0);
  if (size > MAX_REPLAY_BODY_BYTES) return null;
  const bytes = new Uint8Array(size);
  let offset = 0;
  for (const part of raw) {
    bytes.set(new Uint8Array(part.bytes), offset);
    offset += part.bytes.byteLength;
  }
  return toBase64(bytes);
}

chrome.webRequest.onBeforeRequest.addListener(
  (details) => {
    const entry = track(details.url);
    entry.method = details.method;
    entry.body = details.method === "POST" ? encodeBody(details.requestBody) : null;
    entry.replayable = details.method !== "POST" || entry.body !== null;
  },
  { urls: ["http://*/*", "https://*/*"], types: TRACKED_TYPES },
  ["requestBody"],
);

chrome.webRequest.onSendHeaders.addListener(
  (details) => {
    const entry = track(details.url);
    const headers = {};
    for (const h of details.requestHeaders ?? []) {
      if (h.value !== undefined && !NOT_FORWARDED.test(h.name)) headers[h.name] = h.value;
    }
    entry.headers = headers;
    entry.referrer = header(details.requestHeaders, "referer");
    // A multipart form cannot be rebuilt from Chrome's parsed fields.
    if (/^multipart\//i.test(header(details.requestHeaders, "content-type") ?? "")) entry.replayable = false;
  },
  { urls: ["http://*/*", "https://*/*"], types: TRACKED_TYPES },
  chrome.webRequest.OnSendHeadersOptions?.EXTRA_HEADERS ? ["requestHeaders", "extraHeaders"] : ["requestHeaders"],
);

// ---------------------------------------------------------------- download interception

const handled = new Set();

async function handleDownload(item, suggestedName) {
  if (handled.has(item.id)) return;
  handled.add(item.id);
  setTimeout(() => handled.delete(item.id), 60000);
  if (!interceptionActive() || Date.now() < state.bypassUntil) return;
  if (item.byExtensionId === chrome.runtime.id) return;
  const url = item.finalUrl || item.url;
  if (!/^https?:/i.test(url) || siteExcluded(url)) return;

  const name = baseName(suggestedName) || baseName(new URL(url).pathname);
  const ext = extOf(name);
  // A download without a usable extension is still a download unless it is plainly a page or an image.
  const catchable = ext ? state.config.catchExtensions.includes(ext) : !/^(text|image)\//i.test(item.mime || "");
  if (!catchable) return;
  const sent = tracked.get(url) ?? tracked.get(item.url);
  if (sent && !sent.replayable) return; // the browser keeps a POST the app could not repeat
  const size = item.fileSize > 0 ? item.fileSize : item.totalBytes > 0 ? item.totalBytes : null;
  if (state.config.minSizeBytes && size && size < state.config.minSizeBytes) return;

  await chrome.downloads.pause(item.id).catch(() => {});
  const result = await request(
    {
      type: "download",
      url,
      referrer: item.referrer || sent?.referrer || null,
      cookies: await cookieHeader(url),
      userAgent: navigator.userAgent,
      headers: sent?.headers ?? null,
      method: sent?.method === "POST" ? "POST" : null,
      body: sent?.body ?? null,
      fileName: name || null,
      size,
      mime: item.mime || null,
    },
    false,
  );
  if (result.accepted) {
    await chrome.downloads.cancel(item.id).catch(() => {});
    await chrome.downloads.erase({ id: item.id }).catch(() => {});
  } else {
    await chrome.downloads.resume(item.id).catch(() => {});
  }
}

if (chrome.downloads.onDeterminingFilename) {
  // Chromium: the suggested name already reflects Content-Disposition.
  chrome.downloads.onDeterminingFilename.addListener((item, suggest) => {
    handleDownload(item, item.filename).finally(() => {
      try {
        suggest();
      } catch {
        // The download was cancelled after hand-off.
      }
    });
    return true;
  });
} else {
  // Firefox decides the file name before onCreated fires.
  chrome.downloads.onCreated.addListener((item) => handleDownload(item, item.filename));
}

// ---------------------------------------------------------------- media sniffing

// Pre-roll ads are real video files, but never what the button should offer.
const AD_HOSTS = ["doubleclick.net", "googlesyndication.com", "googleadservices.com", "gvt1.com", "imasdk.googleapis.com"];
const SEGMENT_EXT = new Set(["ts", "m4s", "aac", "m4f", "cmfv", "cmfa"]);
const VIDEO_EXT = new Set(["mp4", "webm", "mkv", "mov", "flv", "m4v", "avi", "wmv", "3gp"]);
const AUDIO_EXT = new Set(["mp3", "m4a", "ogg", "opus", "wav", "flac", "weba"]);
const RANGE_PARAMS = ["range", "rn", "rbuf", "bytestart", "byteend", "start", "end"];

function classify(mime, ext) {
  if (mime.includes("mpegurl") || ext === "m3u8") return "hls";
  if (mime === "application/dash+xml" || ext === "mpd") return "dash";
  if (mime === "text/vtt" || ext === "vtt" || ext === "srt") return "subtitle";
  if (SEGMENT_EXT.has(ext) || mime === "video/mp2t" || mime.includes("iso.segment")) return null;
  if (mime.startsWith("video/") || VIDEO_EXT.has(ext)) return "progressive";
  if (mime.startsWith("audio/") || AUDIO_EXT.has(ext)) return "audio";
  return null;
}

function mediaKey(url) {
  const u = new URL(url);
  for (const p of RANGE_PARAMS) u.searchParams.delete(p);
  u.hash = "";
  return u.toString();
}

chrome.webRequest.onHeadersReceived.addListener(
  (details) => {
    if (details.statusCode !== 200 && details.statusCode !== 206) return;
    const mime = (header(details.responseHeaders, "content-type") || "").split(";")[0].trim().toLowerCase();
    let url;
    try {
      url = new URL(details.url);
    } catch {
      return;
    }
    const disposition = header(details.responseHeaders, "content-disposition") ?? "";
    const attachmentName = /filename\*?=(?:UTF-8'')?"?([^";]+)/i.exec(disposition)?.[1];
    const kind = classify(mime, extOf(attachmentName ?? "") || extOf(url.pathname));
    if (!kind || hostMatches(details.url, FRAGMENT_HOSTS) || hostMatches(details.url, AD_HOSTS)) return;
    if (kind === "subtitle" && /sprite|thumb|storyboard/i.test(url.pathname)) return; // seek-bar previews

    let size = null;
    const range = header(details.responseHeaders, "content-range");
    const total = range && /\/(\d+)$/.exec(range);
    if (total) size = Number(total[1]);
    else if (details.statusCode === 200) size = Number(header(details.responseHeaders, "content-length")) || null;
    // Tiny progressive files are usually beacons or preview loops.
    if ((kind === "progressive" || kind === "audio") && size !== null && size < 64 * 1024) return;

    const item = {
      key: mediaKey(details.url),
      url: mediaKey(details.url),
      requestUrl: details.url,
      kind,
      mime: mime || null,
      size,
      frameUrl: details.documentUrl || details.initiator || null,
      seen: Date.now(),
    };
    if (details.tabId >= 0) addMedia(details.tabId, item);
    else tabForWorkerRequest(details).then((tabId) => tabId >= 0 && addMedia(tabId, item));
  },
  { urls: ["http://*/*", "https://*/*"], types: ["media", "xmlhttprequest", "object", "other"] },
  ["responseHeaders"],
);

/** Service workers fetch media with no tab; match them to the page they belong to, preferring the active tab. */
async function tabForWorkerRequest(details) {
  const from = details.initiator || details.documentUrl;
  if (!from || from.startsWith("chrome-extension:") || from.startsWith("moz-extension:")) return -1;
  let origin;
  try {
    origin = new URL(from).origin;
  } catch {
    return -1;
  }
  const tabs = await chrome.tabs.query({ url: origin + "/*" }).catch(() => []);
  return (tabs.find((t) => t.active) ?? tabs[0])?.id ?? -1;
}

function pageOf(url) {
  return url.split(/[?#]/)[0];
}

function addMedia(tabId, item) {
  if (!state.pages.has(tabId)) {
    chrome.tabs.get(tabId).then((tab) => {
      if (tab?.url && !state.pages.has(tabId)) state.pages.set(tabId, pageOf(tab.url));
    }).catch(() => {});
  }
  let list = state.media.get(tabId);
  if (!list) state.media.set(tabId, (list = new Map()));
  const existing = list.get(item.key);
  if (existing) {
    existing.seen = item.seen;
    existing.size ??= item.size;
    return;
  }
  list.set(item.key, item);
  while (list.size > MAX_MEDIA_PER_TAB) list.delete(list.keys().next().value);
  updateBadge(tabId);
  chrome.tabs.sendMessage(tabId, { type: "media-updated", count: list.size }).catch(() => {});
}

function mediaFor(tabId) {
  const rank = { hls: 0, dash: 0, progressive: 1, audio: 2, subtitle: 3 };
  // Players fetch the master playlist first; later playlists in its folder are its qualities, which the app lists.
  const folders = new Set();
  const items = [...(state.media.get(tabId)?.values() ?? [])].filter((item) => {
    if (item.kind !== "hls") return true;
    const folder = item.url.split("?")[0].replace(/[^/]*$/, "");
    if (folders.has(folder)) return false;
    folders.add(folder);
    return true;
  });
  return items.sort((a, b) => rank[a.kind] - rank[b.kind] || (b.size ?? 0) - (a.size ?? 0));
}

function updateBadge(tabId) {
  const count = state.media.get(tabId)?.size ?? 0;
  chrome.action.setBadgeBackgroundColor({ color: "#2563EB", tabId }).catch(() => {});
  chrome.action.setBadgeText({ text: count ? String(count) : "", tabId }).catch(() => {});
}

function updateAllBadges() {
  chrome.action.setTitle({ title: state.connected ? "DownloadManager" : "DownloadManager (app not running)" });
  for (const tabId of state.media.keys()) updateBadge(tabId);
}

chrome.tabs.onUpdated.addListener((tabId, change) => {
  // A new document (or SPA route) starts a fresh media list.
  // Only a new page starts a fresh list: players often change query values or the hash while playing.
  if (!change.url) return;
  const page = pageOf(change.url);
  const previous = state.pages.get(tabId);
  state.pages.set(tabId, page);
  if (previous !== undefined && previous !== page) {
    state.media.delete(tabId);
    updateBadge(tabId);
    chrome.tabs.sendMessage(tabId, { type: "media-updated", count: 0 }).catch(() => {});
  }
});
chrome.tabs.onRemoved.addListener((tabId) => {
  state.media.delete(tabId);
  state.pages.delete(tabId);
});

// ---------------------------------------------------------------- content script & popup messages

chrome.runtime.onMessage.addListener((msg, sender, reply) => {
  const tabId = msg.tabId ?? sender.tab?.id;
  switch (msg.type) {
    case "get-media":
      reply({
        items: mediaFor(tabId),
        connected: state.connected,
        problem: state.connected ? null : state.problem,
        enabled: state.userEnabled,
        showVideoButton: state.config.showVideoButton && state.userEnabled && !siteExcluded(msg.pageUrl || sender.url || ""),
        siteNeedsYtDlp: hostMatches(msg.pageUrl || sender.url || "", YTDLP_SITES),
      });
      return false;

    case "download-media":
      (async () => {
        const seen = state.media.get(tabId)?.get(mediaKey(msg.url));
        const sent = tracked.get(seen?.requestUrl ?? msg.url);
        const result = await request(
          {
            type: "media",
            url: msg.url,
            kind: msg.kind,
            mime: msg.mime || null,
            size: msg.size || null,
            title: msg.title || null,
            pageUrl: msg.pageUrl || null,
            referrer: sent?.referrer || msg.pageUrl || null,
            headers: sent?.headers ?? null,
            cookies: await cookieHeader(msg.url),
            cookieJar: msg.kind === "dash" ? await cookieJar(msg.url) : null,
            userAgent: navigator.userAgent,
          },
          true,
        );
        reply(result);
      })();
      return true;

    case "resolve":
      (async () => {
        reply(
          await request(
            {
              type: "resolve",
              pageUrl: msg.pageUrl,
              referrer: msg.pageUrl,
              cookieJar: await cookieJar(msg.pageUrl),
              userAgent: navigator.userAgent,
            },
            true,
            RESOLVE_TIMEOUT_MS,
          ),
        );
      })();
      return true;

    case "download-format":
      (async () => {
        reply(
          await request(
            {
              type: "format",
              pageUrl: msg.pageUrl,
              url: msg.pageUrl,
              referrer: msg.pageUrl,
              formatSelector: msg.selector,
              quality: msg.label,
              size: msg.size || null,
              title: msg.title || null,
              cookieJar: await cookieJar(msg.pageUrl),
              userAgent: navigator.userAgent,
            },
            true,
          ),
        );
      })();
      return true;

    case "bypass-next":
      state.bypassUntil = Date.now() + 3000;
      return false;

    case "set-enabled":
      state.userEnabled = !!msg.enabled;
      chrome.storage.local.set({ enabled: state.userEnabled });
      reply({ enabled: state.userEnabled });
      return false;

    case "open-app":
      connect(true).then((ok) => {
        if (ok) state.port.postMessage({ type: "show" });
        reply({ ok });
      });
      return true;
  }
  return false;
});

// ---------------------------------------------------------------- context menu

chrome.runtime.onInstalled.addListener(async () => {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({
      id: "dm-link",
      title: "Download with DownloadManager",
      contexts: ["link", "video", "audio", "image"],
    });
  });
  // Existing tabs do not get content scripts until reloaded; inject once on install/update.
  const tabs = await chrome.tabs.query({ url: ["http://*/*", "https://*/*"] });
  for (const tab of tabs) {
    chrome.scripting.executeScript({ target: { tabId: tab.id, allFrames: true }, files: ["content.js"] }).catch(() => {});
  }
});

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  const url = info.linkUrl || info.srcUrl;
  if (!url || !/^https?:/i.test(url)) return;
  const result = await request(
    {
      type: "link",
      url,
      referrer: info.pageUrl || null,
      pageUrl: info.pageUrl || null,
      title: tab?.title || null,
      cookies: await cookieHeader(url),
      userAgent: navigator.userAgent,
    },
    true,
  );
  if (!result.accepted && tab?.id >= 0) {
    chrome.scripting
      .executeScript({
        target: { tabId: tab.id },
        func: (text) => alert(text),
        args: [`DownloadManager: ${result.error || "the app did not accept this link"}`],
      })
      .catch(() => {});
  }
});

// ---------------------------------------------------------------- startup

chrome.storage.local.get({ enabled: true }).then((s) => {
  state.userEnabled = s.enabled;
});
connect(false);
