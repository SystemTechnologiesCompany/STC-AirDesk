"use strict";

const calibrateButton = document.querySelector("#calibrate");
const practiceButton = document.querySelector("#practice");
const practiceKind = document.querySelector("#practice-kind");
const calibrationPanel = document.querySelector("#calibration");
const calibrationStep = document.querySelector("#calibration-step");
const calibrationProgress = document.querySelector("#calibration-progress");
const calibrationName = document.querySelector("#calibration-name");
const captureButton = document.querySelector("#calibration-capture");
const practiceField = document.querySelector("#practice-field");
const practiceTask = document.querySelector("#practice-task");
const practiceScore = document.querySelector("#practice-score");
const practiceTarget = document.querySelector("#practice-target");
const practiceDrop = document.querySelector("#practice-drop");
const practiceCursor = document.querySelector("#practice-cursor");
const typingDrill = document.querySelector("#typing-drill");
const practiceWord = document.querySelector("#practice-word");
const practiceTyped = document.querySelector("#practice-typed");
const practiceKeyboard = document.querySelector("#practice-keyboard");
const scrollDrill = document.querySelector("#scroll-drill");
const mouseResult = document.querySelector("#mouse-result");
const practiceResult = document.querySelector("#practice-result");
const practiceGesture = document.querySelector("#practice-gesture");
const trackingFps = document.querySelector("#tracking-fps");
const defaultProfile = [0.22, 0.5, 0.78, 0.18, 0.5, 0.82, 0.46, 0.46, 0.46];
let mouseProfile = defaultProfile;
let calibration;
let practice;
let fpsStart;
let fpsFrames = 0;
const calibrationSteps = [
  "Point at a comfortable center with your thumb relaxed. Keep it away from your fingertips.",
  "Point to the upper-left corner of your comfortable reach.",
  "Point to the lower-right corner of your comfortable reach.",
  "Touch your thumb to your index fingertip.",
  "Touch your thumb to your middle fingertip.",
  "Touch your thumb to your ring fingertip.",
];

function validProfile(values) {
  return values.length === 9 && values.every(value => Number.isFinite(value) && value >= 0 && value <= 1) &&
    [0, 1, 3, 4].every(index => values[index + 1] - values[index] >= 0.0649) &&
    values.slice(6).every(value => value >= 0.25 && value <= 0.7);
}

function setMouseResult(text) {
  mouseResult.hidden = !text;
  mouseResult.textContent = text;
}

function setPracticeResult(text) {
  practiceResult.hidden = !text;
  practiceResult.textContent = text;
}

function reportPracticeGesture(gesture) {
  const text = "Last gesture: " + gesture;
  if (practiceGesture.textContent !== text) practiceGesture.textContent = text;
  practiceGesture.hidden = false;
}

function updateTrainingButtons() {
  const busy = mouseState.changing || !!calibration;
  calibrateButton.disabled = !settingsLoaded || profileBusy || !mouseState.cameraReady || busy || !!practice;
  practiceButton.disabled = !mouseState.cameraReady || (busy && !practice);
  practiceKind.disabled = practiceButton.disabled || !!practice;
  practiceButton.textContent = practice ? "End practice" : "Start practice";
  updateProfileControls();
}

function updateTrackingFps(timestamp) {
  fpsStart ??= timestamp;
  fpsFrames++;
  if (timestamp - fpsStart < 600) return;
  trackingFps.hidden = false;
  trackingFps.textContent = Math.round((fpsFrames - 1) * 1000 / (timestamp - fpsStart)) + " tracking FPS";
  fpsStart = timestamp;
  fpsFrames = 1;
}

function cancelCalibration(text) {
  if (!calibration) return;
  calibration = undefined;
  calibrationPanel.hidden = true;
  setMouseResult(text);
  updateMouseButton();
}

function resetTraining() {
  endPractice();
  cancelCalibration("");
  trackingFps.hidden = true;
  fpsStart = undefined;
  fpsFrames = 0;
}

function showCalibrationStep() {
  calibrationStep.textContent = (calibration.index + 1) + " / 6 · " + calibrationSteps[calibration.index];
  calibrationProgress.textContent = calibration.index === 0 && !calibration.name
    ? "Name this setup, then select Capture pose."
    : "Select Capture pose, then hold the pose until the next step.";
  captureButton.disabled = false;
}

async function startCalibration(createNew = false) {
  if (mouseState.active) await disableMouseMode();
  if (!settingsLoaded || profileBusy || !mouseState.cameraReady || mouseState.active || practice || calibration) return;
  const existing = createNew ? "" : settingsState.activeProfile;
  calibration = { index: 0, poses: [], samples: null, name: existing };
  calibrationName.value = existing;
  calibrationName.readOnly = !!existing;
  calibrationPanel.hidden = false;
  setMouseResult("");
  showCalibrationStep();
  updateMouseButton();
  calibrationPanel.scrollIntoView({ block: "nearest" });
  if (!existing) calibrationName.focus();
}

function sampleCalibration(hand, timestamp, aspect) {
  if (!calibration.samples) return;
  if (!hand) {
    calibration.samples.length = 0;
    calibrationProgress.textContent = "Show one hand to the camera.";
    return;
  }
  const values = [1 - hand[8].x, hand[8].y, ...pinchRatios(hand, aspect)];
  const samples = calibration.samples;
  if (samples.length && Math.hypot(values[0] - samples[0].values[0], values[1] - samples[0].values[1]) > 0.045) {
    samples.length = 0;
  }
  samples.push({ timestamp, values });
  const duration = timestamp - samples[0].timestamp;
  calibrationProgress.textContent = duration < 550 ? "Hold still…" : "Capturing pose…";
  if (duration < 750 || samples.length < 5) return;
  const median = values.map((_, index) => {
    const ordered = samples.map(sample => sample.values[index]).sort((a, b) => a - b);
    return ordered[Math.floor(ordered.length / 2)];
  });
  const index = calibration.index;
  const poseAccepted = index === 0 ? median.slice(2).every(value => value >= 0.49)
    : index < 3 || median[index - 1] <= 0.51;
  calibration.samples = null;
  if (!poseAccepted) {
    calibrationProgress.textContent = index === 0 ? "Move your thumb farther from your fingertips and try again."
      : "Bring those fingertips closer and capture again.";
    captureButton.disabled = false;
    return;
  }
  calibration.poses.push(median);
  calibration.index++;
  if (calibration.index < calibrationSteps.length) {
    showCalibrationStep();
    return;
  }
  const [center, upper, lower, indexPinch, middlePinch, ringPinch] = calibration.poses;
  const limit = (pose, finger) => Math.max(0.25, Math.min(0.7, (pose[finger] + center[finger]) / 2, pose[finger] + 0.18, center[finger] - 0.1));
  const profile = [
    upper[0], center[0], lower[0], upper[1], center[1], lower[1],
    limit(indexPinch, 2), limit(middlePinch, 3), limit(ringPinch, 4),
  ].map(value => Number(value.toFixed(3)));
  if (!validProfile(profile)) {
    cancelCalibration("The corners need to be farther apart on both axes. Run calibration again.");
    return;
  }
  const name = calibration.name;
  cancelCalibration("Saving calibration…");
  void saveCalibrationProfile(name, profile).then(saved => {
    setMouseResult(saved ? "Saved calibration: " + name : "Calibration applied for this session. Saving failed.");
  });
}

const practiceTasks = [
  { action: 2, label: "Left-click the target", marker: "L" },
  { action: 3, label: "Right-click the target", marker: "R" },
  { action: 4, label: "Double-click the target", marker: "2×" },
  { action: 1, label: "Drag the target into the blue circle", marker: "↔" },
];

function placePracticeMarker(element, point) {
  element.style.left = point.x * 100 + "%";
  element.style.top = point.y * 100 + "%";
}

function nextTarget() {
  const task = practiceTasks[practice.points % practiceTasks.length];
  const fromLeft = practice.points % 2 === 0;
  practice.target = { x: (fromLeft ? 0.19 : 0.62) + Math.random() * 0.19, y: 0.32 + Math.random() * 0.46 };
  practice.drop = { x: 1 - practice.target.x, y: practice.target.y };
  practice.grabbed = false;
  practiceTarget.dataset.grabbed = "false";
  practiceTarget.textContent = task.marker;
  practiceDrop.hidden = task.action !== 1;
  placePracticeMarker(practiceTarget, practice.target);
  placePracticeMarker(practiceDrop, practice.drop);
  practiceTask.textContent = task.label;
}

function updatePracticeClock() {
  if (!practice) return;
  const remaining = Math.max(0, Math.ceil((practice.deadline - performance.now()) / 1000));
  practiceScore.textContent = remaining + "s · " + practice.points + " hits";
  if (!remaining) endPractice(true);
}

async function startPractice(kind = practiceKind.value) {
  if (mouseState.active) await disableMouseMode();
  if (!mouseState.cameraReady || mouseState.active || calibration || practice) return;
  resetMouseGesture();
  mouseState.keyboardPose = null;
  practice = { kind, points: 0, grabbed: false };
  setPracticeResult("");
  reportPracticeGesture("Waiting");
  practiceField.hidden = false;
  practiceTarget.hidden = kind !== "targets";
  practiceDrop.hidden = true;
  typingDrill.hidden = kind !== "typing";
  scrollDrill.hidden = kind !== "scroll";
  if (kind === "typing") {
    practice.word = "hello";
    practice.typed = "";
    practice.keyboardOpen = false;
    practice.shift = false;
    practiceWord.textContent = "Type: " + practice.word;
    practiceTyped.textContent = "";
    practiceTask.textContent = "Touch both index fingertips together to open the keyboard";
    practiceScore.textContent = "";
    practiceKeyboard.hidden = true;
    mouseMessage("Show both hands to start the typing drill.");
  } else if (kind === "scroll") {
    practice.scrollDirection = 6;
    scrollDrill.scrollTop = 0;
    practiceTask.textContent = "Hold a V and move your hand down to the bottom";
    practiceScore.textContent = "0% down";
    mouseMessage("Raise your index and middle fingers. Curl the other fingers.");
  } else {
    practice.deadline = performance.now() + 30000;
    practice.timer = setInterval(updatePracticeClock, 150);
    mouseMessage("Show one hand to aim at the target.");
    nextTarget();
    updatePracticeClock();
  }
  updateMouseButton();
}

function endPractice(finished = false, result) {
  if (!practice) return;
  const { kind, points } = practice;
  clearInterval(practice.timer);
  practice = undefined;
  practiceField.hidden = true;
  practiceCursor.hidden = true;
  resetMouseGesture();
  mouseState.keyboardPose = null;
  mouseMessage("Mouse Mode is off.");
  const targetsResult = (finished ? "Time's up. " : "Practice ended. ") + points + " target" + (points === 1 ? "" : "s") + " hit.";
  setPracticeResult(result ?? (kind === "targets" ? targetsResult : "Practice ended."));
  updateMouseButton();
}

function openPracticeKeyboard() {
  practice.keyboardOpen = true;
  practiceKeyboard.replaceChildren();
  for (const labels of ["1234567890", "qwertyuiop", "asdfghjkl", "zxcvbnm", ["Shift", "Space", "Backspace", "Enter"]]) {
    const row = document.createElement("div");
    row.className = "practice-key-row";
    for (const label of labels) {
      const key = document.createElement("span");
      key.className = "practice-key";
      key.dataset.key = label;
      if (label.length > 1) key.dataset.wide = "";
      key.textContent = label;
      row.append(key);
    }
    practiceKeyboard.append(row);
  }
  practiceKeyboard.hidden = false;
  practiceTask.textContent = "Point at a key and pinch to type";
}

function updatePracticeShift(shift) {
  practice.shift = shift;
  for (const item of practiceKeyboard.querySelectorAll("[data-key]")) {
    const label = item.dataset.key;
    if (label.length === 1) item.textContent = shift ? label.toUpperCase() : label;
    if (label === "Shift") item.dataset.shift = String(shift);
  }
}

function typePracticeKey(key) {
  if (key === "Shift") {
    updatePracticeShift(!practice.shift);
  } else if (key === "Backspace") {
    practice.typed = practice.typed.slice(0, -1);
  } else if (key === "Enter") {
    setPracticeResult("Match the word above. Use Backspace to correct a letter.");
  } else if (practice.typed.length < 24) {
    practice.typed += key === "Space" ? " " : practice.shift ? key.toUpperCase() : key;
    if (practice.shift && /^[a-z]$/.test(key)) updatePracticeShift(false);
  }
  practiceTyped.textContent = practice.typed;
  if (practice.typed === practice.word) endPractice(true, "Word complete: " + practice.word + "!");
}

function practiceTyping(position, action) {
  if (!practice.keyboardOpen) return;
  let key;
  if (position) {
    const field = practiceField.getBoundingClientRect();
    const x = field.left + position.x * (field.width - 1);
    const y = field.top + position.y * (field.height - 1);
    const hit = document.elementFromPoint(x, y);
    if (hit?.dataset.key && practiceKeyboard.contains(hit)) key = hit;
  }
  if (key !== practice.aimedKey) {
    if (practice.aimedKey) practice.aimedKey.dataset.aimed = "false";
    if (key) key.dataset.aimed = "true";
    practice.aimedKey = key;
  }
  if (key && action === 2) typePracticeKey(key.dataset.key);
}

function practiceScroll(action) {
  if (action !== 5 && action !== 6) return;
  const maximum = scrollDrill.scrollHeight - scrollDrill.clientHeight;
  if (maximum <= 0) return;
  scrollDrill.scrollTop += (action === 6 ? 1 : -1) * Math.ceil(maximum / 5);
  const progress = Math.round(scrollDrill.scrollTop * 100 / maximum);
  if (practice.scrollDirection === 6 && progress >= 100) {
    practice.scrollDirection = 5;
    practiceTask.textContent = "Keep the V and move your hand up to return to the top";
  } else if (practice.scrollDirection === 5 && progress <= 0) {
    endPractice(true, "Scroll drill complete. You reached the bottom and returned to the top.");
    return;
  }
  practiceScore.textContent = practice.scrollDirection === 6 ? progress + "% down" : (100 - progress) + "% back up";
}

function practiceFrame(position, action) {
  if (!practice) return;
  practiceCursor.hidden = !position || practice.kind === "scroll";
  if (position && !practiceCursor.hidden) placePracticeMarker(practiceCursor, position);
  if (practice.kind === "typing") {
    practiceTyping(position, action);
    return;
  }
  if (practice.kind === "scroll") {
    practiceScroll(action);
    return;
  }
  if (!position) {
    practice.grabbed = false;
    practice.previousAction = 0;
    practiceTarget.dataset.grabbed = "false";
    placePracticeMarker(practiceTarget, practice.target);
    return;
  }
  const task = practiceTasks[practice.points % practiceTasks.length];
  const closeTo = (point, radius) => Math.hypot(
    (position.x - point.x) * practiceField.clientWidth,
    (position.y - point.y) * practiceField.clientHeight,
  ) <= radius;
  let hit = false;
  if (task.action === 1) {
    if (action === 1 && practice.previousAction !== 1 && closeTo(practice.target, 30)) practice.grabbed = true;
    if (practice.grabbed && action === 1) {
      practiceTarget.dataset.grabbed = "true";
      placePracticeMarker(practiceTarget, position);
    } else if (practice.grabbed) {
      hit = closeTo(practice.drop, 35);
      practice.grabbed = false;
      practiceTarget.dataset.grabbed = "false";
      placePracticeMarker(practiceTarget, practice.target);
    }
  } else {
    hit = action === task.action && closeTo(practice.target, 30);
  }
  practice.previousAction = action;
  if (!hit) {
    if (action >= 2) {
      setPracticeResult(["Left click", "Right click", "Double-click"][action - 2] + " detected · target missed.");
    }
    return;
  }
  practice.points++;
  setPracticeResult("Target hit · " + task.label);
  nextTarget();
  updatePracticeClock();
}

calibrateButton.addEventListener("click", () => startCalibration());
practiceButton.addEventListener("click", () => practice ? endPractice() : startPractice());
captureButton.addEventListener("click", () => {
  if (!calibration) return;
  if (!calibration.name) {
    const name = calibrationName.value.trim();
    const problem = calibrationNameError(name);
    if (problem) {
      calibrationProgress.textContent = problem;
      calibrationName.focus();
      return;
    }
    calibration.name = name;
    calibrationName.value = name;
    calibrationName.readOnly = true;
  }
  calibration.samples = [];
  captureButton.disabled = true;
  calibrationProgress.textContent = "Hold the requested pose.";
});
document.querySelector("#calibration-cancel").addEventListener("click", () => cancelCalibration("Calibration canceled."));
document.addEventListener("keydown", (event) => {
  if (event.key !== "Escape") return;
  endPractice();
  cancelCalibration("Calibration canceled.");
});
