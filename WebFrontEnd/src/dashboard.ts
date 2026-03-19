interface LogEntry {
  timestamp: string;
  level: string;
  message: string;
}

const logsDiv = document.getElementById("logs")!;
const statusDiv = document.getElementById("status")!;
const MAX_VISIBLE_LOGS = 500;

function addLog(entry: LogEntry): void {
  const row = document.createElement("div");
  row.className = "log-entry";

  const ts = document.createElement("span");
  ts.className = "timestamp";
  ts.setAttribute("aria-hidden", "true");
  ts.textContent = "[" + new Date(entry.timestamp).toLocaleTimeString() + "]";

  const lvl = document.createElement("span");
  lvl.className = "level level-" + entry.level.toLowerCase();
  lvl.textContent = entry.level;

  const msg = document.createElement("span");
  msg.className = "message";
  msg.textContent = entry.message;

  row.appendChild(ts);
  row.appendChild(lvl);
  row.appendChild(msg);

  logsDiv.insertBefore(row, logsDiv.firstChild);
  if (logsDiv.children.length > MAX_VISIBLE_LOGS) {
    logsDiv.removeChild(logsDiv.lastChild!);
  }
}

function setStatus(online: boolean): void {
  statusDiv.textContent = online ? "ONLINE" : "OFFLINE";
  statusDiv.className =
    "text-xs bg-gray-800 px-2 py-1 rounded " +
    (online ? "text-green-500" : "text-red-500");
}

logsDiv.addEventListener("keydown", (e: KeyboardEvent) => {
  if (e.key === "ArrowDown") {
    logsDiv.scrollTop += 40;
    e.preventDefault();
  } else if (e.key === "ArrowUp") {
    logsDiv.scrollTop -= 40;
    e.preventDefault();
  }
});

async function init(): Promise<void> {
  try {
    const res = await fetch("/logs");
    const entries: LogEntry[] = await res.json();
    entries.forEach(addLog);
    setStatus(true);
  } catch (err) {
    console.error("Failed to fetch initial logs", err);
  }

  const source = new EventSource("/stream");
  source.onmessage = (e) => addLog(JSON.parse(e.data));
  source.onopen = () => setStatus(true);
  source.onerror = () => setStatus(false);
}

init();
