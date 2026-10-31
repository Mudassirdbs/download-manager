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
  if (!state.config.catchExtensions.includes(extOf(name))) return;
  const size = item.fileSize > 0 ? item.fileSize : item.totalBytes > 0 ? item.totalBytes : null;
  if (state.config.minSizeBytes && size && size < state.config.minSizeBytes) return;

  await chrome.downloads.pause(item.id).catch(() => {});
  const result = await request(
    {
      type: "download",
      url,
      referrer: item.referrer || null,
      cookies: await cookieHeader(url),
      userAgent: navigator.userAgent,
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
    if (details.tabId < 0 || (details.statusCode !== 200 && details.statusCode !== 206)) return;
    const mime = (header(details.responseHeaders, "content-type") || "").split(";")[0].trim().toLowerCase();
    let url;
    try {
      url = new URL(details.url);
    } catch {
      return;
    }
    const kind = classify(mime, extOf(url.pathname));
    if (!kind || hostMatches(details.url, FRAGMENT_HOSTS)) return;

    let size = null;
    const range = header(details.responseHeaders, "content-range");
    const total = range && /\/(\d+)$/.exec(range);
    if (total) size = Number(total[1]);
    else if (details.statusCode === 200) size = Number(header(details.responseHeaders, "content-length")) || null;
    // Tiny progressive files are usually beacons or preview loops.
    if ((kind === "progressive" || kind === "audio") && size !== null && size < 64 * 1024) return;

    addMedia(details.tabId, {
      key: mediaKey(details.url),
      url: mediaKey(details.url),
      kind,
      mime: mime || null,
      size,
      frameUrl: details.documentUrl || details.initiator || null,
      seen: Date.now(),
    });
  },
  { urls: ["http://*/*", "https://*/*"], types: ["media", "xmlhttprequest", "object", "other"] },
  ["responseHeaders"],
);

function addMedia(tabId, item) {
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
  return [...(state.media.get(tabId)?.values() ?? [])].sort((a, b) => (b.size ?? 0) - (a.size ?? 0));
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
  if (change.url) {
    state.media.delete(tabId);
    updateBadge(tabId);
  }
});
chrome.tabs.onRemoved.addListener((tabId) => state.media.delete(tabId));

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
        const result = await request(
          {
            type: "media",
            url: msg.url,
            kind: msg.kind,
            mime: msg.mime || null,
            size: msg.size || null,
            title: msg.title || null,
            pageUrl: msg.pageUrl || null,
            referrer: msg.pageUrl || null,
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
