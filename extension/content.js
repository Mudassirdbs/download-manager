// DownloadManager Integration — content script (every frame).
// Puts a "Download" button over the largest visible video/audio player and shows the media the
// background has seen for this tab when it is clicked.

(() => {
  if (window.__dmContent) return;
  window.__dmContent = true;

  const MIN_WIDTH = 200;
  const MIN_HEIGHT = 112;

  let target = null; // the player element the button is attached to
  let showButton = true;
  let host, root, button, menu;
  let frame = 0;

  // ------------------------------------------------------------ overlay UI (closed shadow root)

  function buildOverlay() {
    host = document.createElement("dm-overlay");
    host.style.cssText = "all:initial;position:fixed;z-index:2147483647;top:0;left:0;pointer-events:none;";
    root = host.attachShadow({ mode: "closed" });
    root.innerHTML = `
      <style>
        :host { all: initial; }
        .btn { pointer-events: auto; display: flex; align-items: center; gap: 6px; height: 30px; padding: 0 12px 0 8px;
          border: 0; border-radius: 15px; background: rgba(37,99,235,.92); color: #fff; cursor: pointer;
          font: 600 12px/30px system-ui, "Segoe UI", sans-serif; box-shadow: 0 2px 8px rgba(0,0,0,.35);
          opacity: .55; transition: opacity .15s; white-space: nowrap; }
        .btn:hover, .btn.open { opacity: 1; }
        .btn svg { width: 16px; height: 16px; flex: none; }
        .menu { pointer-events: auto; position: absolute; top: 36px; right: 0; min-width: 300px; max-width: 460px;
          max-height: 320px; overflow: auto; background: #fff; color: #111827; border-radius: 8px;
          box-shadow: 0 8px 24px rgba(0,0,0,.3); font: 13px/1.35 system-ui, "Segoe UI", sans-serif; padding: 4px 0; }
        .menu[hidden] { display: none; }
        .item { display: block; width: 100%; text-align: left; border: 0; background: none; padding: 7px 12px;
          cursor: pointer; color: inherit; font: inherit; }
        .item:hover:not(:disabled) { background: #EFF6FF; }
        .item:disabled { color: #9CA3AF; cursor: default; }
        .meta { color: #6B7280; font-size: 11px; display: block; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
        .note { padding: 8px 12px; color: #6B7280; }
        .status { padding: 8px 12px; color: #065F46; }
        .status.err { color: #B91C1C; }
      </style>
      <div class="wrap" style="position:relative">
        <button class="btn" type="button" title="Download with DownloadManager">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round">
            <path d="M12 4v11M7 10l5 5 5-5M5 20h14"/></svg>
          <span>Download</span>
        </button>
        <div class="menu" hidden></div>
      </div>`;
    button = root.querySelector(".btn");
    menu = root.querySelector(".menu");
    button.addEventListener("click", (e) => {
      e.preventDefault();
      e.stopPropagation();
      menu.hidden ? openMenu() : closeMenu();
    });
    document.addEventListener("pointerdown", (e) => {
      if (!e.composedPath().includes(host)) closeMenu();
    }, true);
    setVisible(false);
    (document.fullscreenElement || document.documentElement).appendChild(host);
  }

  function setVisible(visible) {
    if (host) host.style.display = visible ? "block" : "none";
    if (!visible) closeMenu();
  }

  function closeMenu() {
    if (!menu) return;
    menu.hidden = true;
    button.classList.remove("open");
  }

  // ------------------------------------------------------------ choosing the player

  function visibleArea(el) {
    const r = el.getBoundingClientRect();
    if (r.width < MIN_WIDTH || r.height < MIN_HEIGHT) return 0;
    const style = getComputedStyle(el);
    if (style.visibility === "hidden" || style.display === "none" || Number(style.opacity) === 0) return 0;
    const w = Math.min(r.right, innerWidth) - Math.max(r.left, 0);
    const h = Math.min(r.bottom, innerHeight) - Math.max(r.top, 0);
    return w > 0 && h > 0 ? w * h : 0;
  }

  function pickTarget() {
    let best = null;
    let bestArea = 0;
    for (const el of document.querySelectorAll("video, audio")) {
      const area = visibleArea(el);
      if (area > bestArea) {
        best = el;
        bestArea = area;
      }
    }
    return best;
  }

  // ------------------------------------------------------------ positioning

  function schedule() {
    if (!frame) frame = requestAnimationFrame(update);
  }

  function update() {
    frame = 0;
    if (!showButton) {
      setVisible(false);
      return;
    }
    const next = pickTarget();
    if (next !== target) {
      if (target) resize.unobserve(target);
      target = next;
      if (target) resize.observe(target);
      closeMenu();
    }
    if (!target) {
      setVisible(false);
      return;
    }
    if (!host) buildOverlay();

    // In fullscreen only the fullscreen element's subtree is painted; follow it unless it is the video itself.
    const fs = document.fullscreenElement;
    const parent = fs && fs !== target && !(fs instanceof HTMLMediaElement) ? fs : document.documentElement;
    if (fs === target) {
      setVisible(false);
      return;
    }
    if (host.parentNode !== parent) parent.appendChild(host);

    const r = target.getBoundingClientRect();
    const top = Math.max(r.top, 0) + 10;
    const right = Math.min(r.right, innerWidth) - 10;
    host.style.top = `${top}px`;
    host.style.left = `${right}px`;
    host.style.transform = "translateX(-100%)";
    setVisible(true);
  }

  const resize = new ResizeObserver(schedule);
  let mutationTimer = 0;
  const mutations = new MutationObserver(() => {
    // Pages mutate constantly; re-evaluate the player at most every 250 ms.
    if (!mutationTimer) mutationTimer = setTimeout(() => ((mutationTimer = 0), schedule()), 250);
  });

  // ------------------------------------------------------------ menu

  function pageTitle() {
    let title = "";
    try {
      title = window.top.document.title;
    } catch {
      // cross-origin frame
    }
    title ||= document.title || document.querySelector('meta[property="og:title"]')?.content || "";
    return title.replace(/^\(\d+\)\s*/, "").replace(/\s+[-|–—]\s+[^-|–—]+$/, "").trim();
  }

  function directSource() {
    const src = target?.currentSrc || target?.src || "";
    return /^https?:/i.test(src) ? src : null;
  }

  function describe(item) {
    const parts = [];
    if (item.kind === "hls") parts.push("HLS stream");
    else if (item.kind === "dash") parts.push("DASH stream");
    else if (item.kind === "audio") parts.push("Audio");
    else if (item.kind === "subtitle") parts.push("Subtitles");
    else parts.push("Video");
    if (item.mime) parts.push(item.mime);
    if (item.size) parts.push(formatBytes(item.size));
    return parts.join(" · ");
  }

  function formatBytes(n) {
    const units = ["B", "KB", "MB", "GB"];
    let i = 0;
    while (n >= 1024 && i < units.length - 1) {
      n /= 1024;
      i++;
    }
    return `${n.toFixed(i ? 1 : 0)} ${units[i]}`;
  }

  async function openMenu() {
    button.classList.add("open");
    menu.hidden = false;
    menu.innerHTML = `<div class="note">Looking for media…</div>`;
    const res = await chrome.runtime.sendMessage({ type: "get-media", pageUrl: location.href }).catch(() => null);
    const items = [...(res?.items ?? [])];
    const direct = directSource();
    if (direct && !items.some((i) => i.url === direct)) {
      items.unshift({ url: direct, kind: target.localName === "audio" ? "audio" : "progressive", mime: null, size: null });
    }

    menu.textContent = "";
    // On sites like YouTube the player's own requests are unusable fragments; ask yt-dlp straight away.
    if (res?.siteNeedsYtDlp) return findFormats();

    if (!items.length) {
      menu.innerHTML = `<div class="note">No downloadable media found yet.<br>Play the video for a few seconds, or find formats below.</div>`;
    }
    for (const item of items) {
      const name = decodeURIComponent(new URL(item.url).pathname.split("/").pop() || item.url);
      menu.append(menuItem(name, describe(item), () => send(item)));
    }
    menu.append(menuItem("Find all formats…", "Ask yt-dlp which qualities this page offers", findFormats));
  }

  function menuItem(text, detail, onClick) {
    const el = document.createElement("button");
    el.className = "item";
    el.type = "button";
    el.textContent = text.length > 60 ? text.slice(0, 57) + "…" : text;
    const meta = document.createElement("span");
    meta.className = "meta";
    meta.textContent = detail;
    el.append(meta);
    el.addEventListener("click", onClick);
    return el;
  }

  async function findFormats() {
    menu.innerHTML = `<div class="note">Finding formats… this can take a few seconds.</div>`;
    const pageUrl = location.href;
    const res = await chrome.runtime.sendMessage({ type: "resolve", pageUrl }).catch(() => null);
    if (menu.hidden) return;
    menu.textContent = "";
    if (!res?.formats?.length) {
      menu.innerHTML = `<div class="status err">${escapeHtml(res?.error || "No formats found on this page")}</div>`;
      return;
    }
    const title = res.title || pageTitle();
    for (const f of res.formats) {
      const detail = [f.audioOnly ? "Audio" : "Video", f.ext?.toUpperCase(), f.size ? `~${formatBytes(f.size)}` : null]
        .filter(Boolean)
        .join(" · ");
      menu.append(menuItem(f.label, detail, () => sendFormat(f, title, pageUrl)));
    }
  }

  async function sendFormat(format, title, pageUrl) {
    menu.innerHTML = `<div class="note">Sending to DownloadManager…</div>`;
    const res = await chrome.runtime
      .sendMessage({ type: "download-format", pageUrl, selector: format.selector, label: format.label, size: format.size, title })
      .catch(() => null);
    showResult(res);
  }

  function showResult(res) {
    const ok = res?.accepted;
    menu.innerHTML = ok
      ? `<div class="status">Sent to DownloadManager.</div>`
      : `<div class="status err">Could not send: ${escapeHtml(res?.error || "the app is not available")}</div>`;
    setTimeout(closeMenu, ok ? 1200 : 4000);
  }

  async function send(item) {
    menu.innerHTML = `<div class="note">Sending to DownloadManager…</div>`;
    const res = await chrome.runtime
      .sendMessage({
        type: "download-media",
        url: item.url,
        kind: item.kind,
        mime: item.mime,
        size: item.size,
        title: pageTitle(),
        pageUrl: location.href,
      })
      .catch(() => null);
    showResult(res);
  }

  function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, (c) => `&#${c.charCodeAt(0)};`);
  }

  // ------------------------------------------------------------ wiring

  async function refreshSettings() {
    const res = await chrome.runtime.sendMessage({ type: "get-media", pageUrl: location.href }).catch(() => null);
    showButton = res ? res.showVideoButton : true;
    schedule();
  }

  chrome.runtime.onMessage.addListener((msg) => {
    if (msg.type === "media-updated") schedule();
  });

  // Alt+click on a link lets the browser download it itself.
  document.addEventListener("click", (e) => {
    if (e.altKey && e.target instanceof Element && e.target.closest("a[href]")) {
      chrome.runtime.sendMessage({ type: "bypass-next" }).catch(() => {});
    }
  }, true);

  addEventListener("scroll", schedule, { passive: true, capture: true });
  addEventListener("resize", schedule, { passive: true });
  document.addEventListener("fullscreenchange", schedule);
  document.addEventListener("play", schedule, true);
  document.addEventListener("loadedmetadata", schedule, true);
  mutations.observe(document.documentElement, { childList: true, subtree: true });
  setInterval(schedule, 1500); // catches layout changes no observer reports (CSS transitions, transforms)
  refreshSettings();
})();
