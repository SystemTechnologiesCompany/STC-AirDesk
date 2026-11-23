"use strict";

const settingsDialog = document.querySelector("#settings-dialog");
const settingsStatus = document.querySelector("#settings-status");
const startupSetting = document.querySelector("#startup-setting");
const startupStatus = document.querySelector("#startup-status");
const profileSelect = document.querySelector("#profile-select");
const newProfileButton = document.querySelector("#new-profile");
const recalibrateButton = document.querySelector("#recalibrate-profile");
const deleteProfileButton = document.querySelector("#delete-profile");
const deleteProfilesButton = document.querySelector("#delete-profiles");
const activeCalibration = document.querySelector("#active-calibration");
const tuningControls = document.querySelector("#tuning-controls");
const tuningForm = document.querySelector("#tuning-form");
const tuningStatus = document.querySelector("#tuning-status");
const tuningProfile = document.querySelector("#tuning-profile");
const tuningFields = ["sensitivity", "smoothing", "scrollSpeed"].map(name => ({
  name,
  input: document.querySelector("#tune-" + name),
  output: document.querySelector('output[for="tune-' + name + '"]'),
}));
let settingsState = { activeProfile: "", profiles: [], keyboard: false, fingerMask: 31, drill: "targets" };
let settingsLoaded = false;
let profileBusy = false;
let settingsWrites = Promise.resolve();

async function settingsRequest(path, body) {
  const response = await fetch(path, {
    method: body === undefined ? "GET" : "POST",
    body,
    signal: AbortSignal.timeout(3000),
  });
  if (!response.ok) throw new Error("Settings request failed.");
  if (body === undefined) return response.json();
}

function applyActiveCalibration() {
  const profile = settingsState.profiles.find(item => item.name === settingsState.activeProfile);
  mouseProfile = profile?.values ?? defaultProfile;
  mouseTuning = (profile ? profile.tuning : settingsState.defaultTuning) ?? standardTuning;
  tuningProfile.textContent = profile ? "Profile: " + profile.name : "Default profile";
  tuningStatus.textContent = "";
  for (const field of tuningFields) {
    field.input.value = mouseTuning[field.name];
    field.output.textContent = field.input.value + "%";
  }
  activeCalibration.textContent = "Calibration: " + (profile?.name ?? "Default");
  updateMouseButton();
}

function refreshProfileList() {
  profileSelect.replaceChildren(new Option("Default (no calibration)", ""));
  for (const profile of settingsState.profiles) profileSelect.add(new Option(profile.name, profile.name));
  profileSelect.value = settingsState.activeProfile;
  updateProfileControls();
}

function updateProfileControls() {
  const blocked = !settingsLoaded || profileBusy || !!calibration || !!practice || mouseState.changing;
  const canCalibrate = !blocked && mouseState.cameraReady;
  profileSelect.disabled = blocked;
  newProfileButton.disabled = !canCalibrate || settingsState.profiles.length >= 30;
  recalibrateButton.disabled = !canCalibrate || !settingsState.activeProfile;
  deleteProfileButton.disabled = blocked || !settingsState.activeProfile;
  deleteProfilesButton.disabled = blocked || !settingsState.profiles.length;
  tuningControls.disabled = blocked;
  palmPauseSetting.disabled = !settingsLoaded;
}

async function loadSettings() {
  try {
    const saved = await settingsRequest("settings");
    settingsState = saved;
    keyboardFeature.checked = saved.keyboard;
    palmPauseSetting.checked = saved.palmPause ?? true;
    choices.forEach((button, index) => {
      button.setAttribute("aria-pressed", String(Boolean(saved.fingerMask & (1 << index))));
    });
    practiceKind.value = saved.drill;
    settingsLoaded = true;
    applyActiveCalibration();
    drawMarkers();
  } catch {
    settingsStatus.textContent = "Saved settings could not be loaded.";
  }
  refreshProfileList();
}

function persistSettings() {
  const snapshot = JSON.stringify(settingsState);
  settingsWrites = settingsWrites.catch(() => {}).then(() => settingsRequest("settings", snapshot));
  return settingsWrites;
}

function savePreferences() {
  if (!settingsLoaded) return;
  settingsState.keyboard = keyboardFeature.checked;
  settingsState.palmPause = palmPauseSetting.checked;
  settingsState.fingerMask = choices.reduce((mask, button, index) =>
    button.getAttribute("aria-pressed") === "true" ? mask | (1 << index) : mask, 0);
  settingsState.drill = practiceKind.value;
  persistSettings().catch(() => {
    setMouseResult("Preferences changed for this session. Saving failed.");
  });
}

async function changeProfiles(change, message, stopMouse = true) {
  if (!settingsLoaded || profileBusy) return false;
  profileBusy = true;
  updateProfileControls();
  try {
    if (stopMouse && mouseState.active) await disableMouseMode();
    change();
    applyActiveCalibration();
    refreshProfileList();
    await persistSettings();
    if (message) settingsStatus.textContent = message;
    return true;
  } catch {
    settingsStatus.textContent = "Changes apply for this session. Saving failed.";
    return false;
  } finally {
    profileBusy = false;
    updateProfileControls();
  }
}

function calibrationNameError(name) {
  if (!name || name.length > 36 || /[\x00-\x1f\x7f-\x9f]/.test(name)) return "Enter a profile name up to 36 characters.";
  if (settingsState.profiles.length >= 30) return "Delete a profile before adding another.";
  if (settingsState.profiles.some(item => item.name.toUpperCase() === name.toUpperCase())) {
    return "That name is already in use. Choose a different profile name.";
  }
  return "";
}

function saveCalibrationProfile(name, values) {
  return changeProfiles(() => {
    const existing = settingsState.profiles.find(item => item.name === name);
    if (existing) existing.values = values;
    else settingsState.profiles.push({ name, values });
    settingsState.activeProfile = name;
  }, "Calibration saved.");
}

document.querySelector("#open-settings").addEventListener("click", async () => {
  if (!settingsDialog.open) settingsDialog.showModal();
  startupSetting.disabled = true;
  try {
    startupSetting.checked = await settingsRequest("startup");
    startupStatus.textContent = "Camera and Mouse Mode start off.";
    startupSetting.disabled = false;
  } catch {
    startupStatus.textContent = "Windows startup settings are unavailable.";
  }
  updateProfileControls();
});
document.querySelector("#close-settings").addEventListener("click", () => settingsDialog.close());
for (const field of tuningFields) {
  field.input.addEventListener("input", () => {
    field.output.textContent = field.input.value + "%";
    tuningStatus.textContent = "Save tuning to apply your changes.";
  });
}

async function saveTuning(tuning) {
  const saved = await changeProfiles(() => {
    const profile = settingsState.profiles.find(item => item.name === settingsState.activeProfile);
    if (profile) profile.tuning = tuning;
    else settingsState.defaultTuning = tuning;
  }, "", false);
  tuningStatus.textContent = saved ? "Tuning saved for this profile." : "Tuning could not be saved.";
}

tuningForm.addEventListener("submit", event => {
  event.preventDefault();
  const values = Object.fromEntries(tuningFields.map(field => [field.name, Number(field.input.value)]));
  void saveTuning(values);
});
document.querySelector("#reset-tuning").addEventListener("click", () => void saveTuning({ ...standardTuning }));
palmPauseSetting.addEventListener("change", () => {
  mouseState.palmHold = null;
  updateMouseButton();
  savePreferences();
});
startupSetting.addEventListener("change", async () => {
  const enabled = startupSetting.checked;
  startupSetting.disabled = true;
  try {
    await settingsRequest("startup", String(enabled));
    startupStatus.textContent = enabled ? "The app will open the next time you sign in." : "Windows startup is off.";
  } catch {
    startupSetting.checked = !enabled;
    startupStatus.textContent = "Could not change Windows startup.";
  } finally {
    startupSetting.disabled = false;
  }
});
profileSelect.addEventListener("change", () => {
  const name = profileSelect.value;
  void changeProfiles(() => { settingsState.activeProfile = name; }, "");
});
newProfileButton.addEventListener("click", () => {
  settingsDialog.close();
  void startCalibration(true);
});
recalibrateButton.addEventListener("click", () => {
  settingsDialog.close();
  void startCalibration();
});
deleteProfileButton.addEventListener("click", () => {
  const name = settingsState.activeProfile;
  if (!name || !window.confirm("Delete the calibration profile “" + name + "”?")) return;
  void changeProfiles(() => {
    settingsState.profiles = settingsState.profiles.filter(item => item.name !== name);
    settingsState.activeProfile = "";
  }, "Profile deleted. Default calibration is active.");
});
deleteProfilesButton.addEventListener("click", () => {
  if (!window.confirm("Delete all saved calibration profiles?")) return;
  void changeProfiles(() => {
    settingsState.profiles = [];
    settingsState.activeProfile = "";
  }, "All profiles deleted. Default calibration is active.");
});
practiceKind.addEventListener("change", savePreferences);
keyboardFeature.addEventListener("change", savePreferences);
void loadSettings();
