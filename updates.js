"use strict";

const updateVersion = document.querySelector("#update-version");
const updateStatus = document.querySelector("#update-status");
const updateRelease = document.querySelector("#update-release");
const updateNotes = document.querySelector("#update-notes");
const updateProgress = document.querySelector("#update-progress");
const checkUpdates = document.querySelector("#check-updates");
const downloadUpdate = document.querySelector("#download-update");
const cancelUpdate = document.querySelector("#cancel-update");
let updateOffer = null;
let updateDownload = null;

async function checkForUpdates() {
  checkUpdates.disabled = true;
  downloadUpdate.hidden = true;
  updateRelease.hidden = true;
  updateProgress.hidden = true;
  updateOffer = null;
  updateStatus.textContent = "Checking for updates…";
  try {
    const response = await fetch("updates", { signal: AbortSignal.timeout(20000) });
    if (!response.ok) throw new Error("Check failed");
    const release = await response.json();
    updateVersion.textContent = release.installed;
    if (release.state === "available") {
      updateOffer = release;
      updateStatus.textContent = "Version " + release.version + " is available.";
      downloadUpdate.hidden = false;
    } else if (release.state === "missing-download") {
      updateStatus.textContent = "Version " + release.version + " is listed, but its verified Windows download is unavailable.";
    } else if (release.state === "current") {
      updateStatus.textContent = "You’re up to date.";
    } else if (release.state === "unavailable") {
      updateStatus.textContent = "No public AirDesk release is available yet.";
    } else {
      throw new Error("Check failed");
    }
    if (release.notes) {
      updateNotes.textContent = release.notes;
      updateRelease.hidden = false;
    }
  } catch {
    updateStatus.textContent = "Couldn’t check for updates. Try again in a moment.";
  } finally {
    checkUpdates.disabled = false;
  }
}

async function downloadAirDeskUpdate() {
  const offer = updateOffer;
  if (!offer || updateDownload) return;
  const controller = new AbortController();
  updateDownload = controller;
  checkUpdates.disabled = true;
  downloadUpdate.disabled = true;
  cancelUpdate.hidden = false;
  updateProgress.value = 0;
  updateProgress.hidden = false;
  updateStatus.textContent = "Downloading version " + offer.version + "…";
  try {
    const response = await fetch("update-download", { method: "POST", body: offer.version, signal: controller.signal });
    if (!response.ok || !response.body || Number(response.headers.get("Content-Length")) !== offer.size)
      throw new Error("Download failed");
    const reader = response.body.getReader();
    const bytes = new Uint8Array(offer.size);
    let received = 0;
    try {
      for (;;) {
        const { value, done } = await reader.read();
        if (done) break;
        if (received + value.length > bytes.length) throw new Error("Download is larger than expected");
        bytes.set(value, received);
        received += value.length;
        updateProgress.value = Math.floor(100 * received / offer.size);
      }
    } finally {
      reader.releaseLock();
    }
    if (received !== bytes.length) throw new Error("Incomplete download");
    updateStatus.textContent = "Verifying the download…";
    const digest = await crypto.subtle.digest("SHA-256", bytes);
    const hash = Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, "0")).join("");
    if (controller.signal.aborted) return;
    if (hash !== offer.sha256) {
      updateStatus.textContent = "The checksum didn’t match. The file wasn’t saved. Check for updates again.";
      downloadUpdate.hidden = true;
      updateOffer = null;
      return;
    }

    const url = URL.createObjectURL(new Blob([bytes], { type: "application/octet-stream" }));
    const link = document.createElement("a");
    link.href = url;
    link.download = "STC-AirDesk-" + offer.version + ".exe";
    document.body.append(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 60000);
    updateStatus.textContent = "Download verified. Look for " + link.download + " in Downloads.";
  } catch {
    updateStatus.textContent = controller.signal.aborted ? "Download canceled." : "The update couldn’t be downloaded. Try again.";
  } finally {
    if (controller.signal.aborted) updateStatus.textContent = "Download canceled.";
    controller.abort();
    updateDownload = null;
    checkUpdates.disabled = false;
    downloadUpdate.disabled = false;
    cancelUpdate.hidden = true;
    updateProgress.hidden = true;
  }
}

checkUpdates.addEventListener("click", checkForUpdates);
downloadUpdate.addEventListener("click", downloadAirDeskUpdate);
cancelUpdate.addEventListener("click", () => updateDownload?.abort());
fetch("update-version", { signal: AbortSignal.timeout(3000) })
  .then(response => response.ok ? response.text() : null)
  .then(version => { if (version) updateVersion.textContent = version; })
  .catch(() => {});
