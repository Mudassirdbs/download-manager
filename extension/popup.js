const $ = (id) => document.getElementById(id);

function formatBytes(n) {
  if (!n) return "";
  const units = ["B", "KB", "MB", "GB"];
  let i = 0;
  while (n >= 1024 && i < units.length - 1) {
    n /= 1024;
    i++;
  }
  return `${n.toFixed(i ? 1 : 0)} ${units[i]}`;
}

async function render() {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  const res = await chrome.runtime.sendMessage({ type: "get-media", tabId: tab?.id, pageUrl: tab?.url });
  $("dot").classList.toggle("on", res.connected);
  $("status").textContent = res.connected ? "Connected to the desktop app" : "Desktop app is not running";
  $("enabled").checked = res.enabled;

  const list = $("list");
  list.textContent = "";
  $("formats").onclick = () => findFormats(tab);
  if (!res.items.length) {
    list.innerHTML = `<div class="empty">No media detected on this page yet.</div>`;
    return;
  }
  for (const item of res.items) {
    const row = document.createElement("div");
    row.className = "item";
    const text = document.createElement("div");
    const name = document.createElement("div");
    name.className = "name";
    name.textContent = decodeURIComponent(new URL(item.url).pathname.split("/").pop() || item.url);
    name.title = item.url;
    const meta = document.createElement("div");
    meta.className = "meta";
    meta.textContent = [item.kind, item.mime, formatBytes(item.size)].filter(Boolean).join(" · ");
    text.append(name, meta);
    const btn = document.createElement("button");
    btn.textContent = "Download";
    btn.addEventListener("click", async () => {
      btn.disabled = true;
      const r = await chrome.runtime.sendMessage({
        type: "download-media",
        url: item.url,
        kind: item.kind,
        mime: item.mime,
        size: item.size,
        title: tab?.title,
        pageUrl: tab?.url,
      });
      btn.textContent = r?.accepted ? "Sent" : "Failed";
    });
    row.append(text, btn);
    list.append(row);
  }
}

async function findFormats(tab) {
  const list = $("list");
  list.innerHTML = `<div class="empty">Finding formats… this can take a few seconds.</div>`;
  const res = await chrome.runtime.sendMessage({ type: "resolve", pageUrl: tab.url });
  list.textContent = "";
  if (!res?.formats?.length) {
    list.innerHTML = `<div class="empty"></div>`;
    list.firstChild.textContent = res?.error || "No formats found on this page.";
    return;
  }
  for (const f of res.formats) {
    const row = document.createElement("div");
    row.className = "item";
    const text = document.createElement("div");
    const name = document.createElement("div");
    name.className = "name";
    name.textContent = f.label;
    const meta = document.createElement("div");
    meta.className = "meta";
    meta.textContent = [f.audioOnly ? "audio" : "video", f.ext, formatBytes(f.size)].filter(Boolean).join(" · ");
    text.append(name, meta);
    const btn = document.createElement("button");
    btn.textContent = "Download";
    btn.addEventListener("click", async () => {
      btn.disabled = true;
      const r = await chrome.runtime.sendMessage({
        type: "download-format", pageUrl: tab.url, selector: f.selector, label: f.label, size: f.size, title: res.title || tab.title,
      });
      btn.textContent = r?.accepted ? "Sent" : "Failed";
    });
    row.append(text, btn);
    list.append(row);
  }
}

$("enabled").addEventListener("change", (e) => chrome.runtime.sendMessage({ type: "set-enabled", enabled: e.target.checked }));
$("open").addEventListener("click", () => chrome.runtime.sendMessage({ type: "open-app" }).then(() => window.close()));
render();
