(function () {
  const logsDiv = document.getElementById("logs");
  const statusDiv = document.getElementById("status");
  const maxVisibleLogs = 500;

  function addLog(entry) {
    const row = document.createElement("div");
    row.className = "log-entry";

    const timestamp = document.createElement("span");
    timestamp.className = "timestamp";
    timestamp.setAttribute("aria-hidden", "true");
    timestamp.textContent = "[" + new Date(entry.timestamp).toLocaleTimeString() + "]";

    const level = document.createElement("span");
    level.className = "level level-" + String(entry.level || "").toLowerCase();
    level.textContent = entry.level;

    const message = document.createElement("span");
    message.className = "message";
    message.textContent = entry.message;

    row.append(timestamp, level, message);
    logsDiv.prepend(row);

    while (logsDiv.children.length > maxVisibleLogs) {
      logsDiv.removeChild(logsDiv.lastChild);
    }
  }

  function setStatus(isOnline) {
    statusDiv.textContent = isOnline ? "ONLINE" : "OFFLINE";
    statusDiv.className = "status " + (isOnline ? "status-online" : "status-offline");
  }

  async function loadInitialLogs() {
    try {
      const response = await fetch("/logs");
      const entries = await response.json();
      entries.forEach(addLog);
      setStatus(true);
    } catch (error) {
      console.error("Failed to fetch initial logs", error);
    }
  }

  function wireKeyboardScrolling() {
    logsDiv.addEventListener("keydown", event => {
      if (event.key === "ArrowDown") {
        logsDiv.scrollTop += 40;
        event.preventDefault();
      }

      if (event.key === "ArrowUp") {
        logsDiv.scrollTop -= 40;
        event.preventDefault();
      }
    });
  }

  function startStream() {
    const source = new EventSource("/stream");
    source.onmessage = event => addLog(JSON.parse(event.data));
    source.onopen = () => setStatus(true);
    source.onerror = () => setStatus(false);
  }

  wireKeyboardScrolling();
  loadInitialLogs().finally(startStream);
})();
