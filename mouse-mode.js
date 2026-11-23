"use strict";

const mouseButton = document.querySelector("#mouse-mode");
const mouseStatus = document.querySelector("#mouse-status");
const mouseArea = document.querySelector("#mouse-area");
const keyboardFeature = document.querySelector("#keyboard-feature");
const pauseMouseButton = document.querySelector("#pause-mouse");
const pauseBanner = document.querySelector("#mouse-pause-banner");
const palmPauseSetting = document.querySelector("#palm-pause-setting");
const standardTuning = { sensitivity: 50, smoothing: 50, scrollSpeed: 50 };
let mouseTuning = standardTuning;
const pinchTips = [8, 12, 16];
const dragDelay = 260;
const mouseState = {
  cameraReady: false,
  active: false,
  changing: false,
  pending: null,
  frames: [],
  wrist: null,
  position: null,
  lastSample: null,
  pinch: null,
  armed: [false, false, false],
  lastLeftClick: null,
  scroll: null,
  keyboardPose: null,
  paused: false,
  palmHold: null,
  palmLatched: false,
  palmReleasedAt: null,
};

function mouseMessage(text) {
  if (mouseStatus.textContent !== text) mouseStatus.textContent = text;
}

function updateMouseButton() {
  mouseButton.disabled = !mouseState.cameraReady || mouseState.changing || !!practice || !!calibration;
  mouseButton.textContent = mouseState.active ? "Disable" : "Enable";
  mouseButton.setAttribute("aria-pressed", String(mouseState.active));
  mouseArea.hidden = !mouseState.active;
  pauseMouseButton.hidden = !mouseState.active;
  pauseMouseButton.disabled = mouseState.changing;
  pauseMouseButton.textContent = mouseState.paused ? "Resume mouse" : "Pause mouse";
  pauseBanner.hidden = !mouseState.active || !mouseState.paused;
  pauseBanner.textContent = palmPauseSetting.checked ? "Mouse paused · Hold an open palm to resume" : "Mouse paused";
  const [left, , right, top, , bottom] = mouseProfile;
  mouseArea.style.inset = top * 100 + "% " + left * 100 + "% " + (1 - bottom) * 100 + "% " + (1 - right) * 100 + "%";
  updateTrainingButtons();
}

function mouseCameraReady(ready) {
  mouseState.cameraReady = ready;
  if (!ready) resetTraining();
  if (!ready && mouseState.active) disableMouseMode();
  mouseMessage(ready ? "Mouse Mode is off." : "Start the camera to enable Mouse Mode.");
  updateMouseButton();
}

async function sendMouseCommand(command) {
  const response = await fetch("mouse", {
    method: "POST",
    body: command,
    signal: AbortSignal.timeout(500),
  });
  if (!response.ok) throw new Error("Mouse control request failed.");
}

function resetMouseGesture() {
  mouseState.wrist = null;
  mouseState.position = null;
  mouseState.lastSample = null;
  mouseState.pinch = null;
  mouseState.armed.fill(false);
  mouseState.lastLeftClick = null;
  mouseState.scroll = null;
}

async function enableMouseMode() {
  if (!mouseState.cameraReady || mouseState.changing) return;
  mouseState.changing = true;
  updateMouseButton();
  try {
    await sendMouseCommand("enable");
    if (!mouseState.cameraReady) {
      await sendMouseCommand("disable");
      return;
    }
    resetMouseGesture();
    mouseState.keyboardPose = null;
    mouseState.palmHold = null;
    mouseState.palmLatched = false;
    mouseState.palmReleasedAt = null;
    mouseState.paused = false;
    mouseState.active = true;
    setMouseResult("");
    mouseMessage("Show one hand to move the cursor.");
  } catch {
    mouseMessage("Mouse Mode could not start. Reopen the Windows app and try again.");
  } finally {
    mouseState.changing = false;
    updateMouseButton();
  }
}

async function disableMouseMode(text = "Mouse Mode is off.") {
  if (!mouseState.active) return;
  mouseState.active = false;
  mouseState.paused = false;
  mouseState.frames.length = 0;
  mouseState.changing = true;
  resetMouseGesture();
  updateMouseButton();
  mouseMessage(text);
  try {
    await mouseState.pending;
    await sendMouseCommand("disable");
  } catch {
    // Windows also releases the button when tracking messages stop arriving.
  } finally {
    mouseState.changing = false;
    updateMouseButton();
  }
}

function flushMouseFrames() {
  if (!mouseState.active || mouseState.pending || !mouseState.frames.length) return;
  const { command, button } = mouseState.frames.shift();
  mouseState.pending = sendMouseCommand(command).then(
    () => {
      mouseState.pending = null;
      if (mouseState.active && button >= 2 && button <= 4) {
        setMouseResult(["Left click", "Right click", "Double-click"][button - 2] + " accepted by Windows.");
      }
      flushMouseFrames();
    },
    () => {
      mouseState.pending = null;
      if (mouseState.active) disableMouseMode("Mouse Mode stopped. Enable it to resume.");
    },
  );
}

function sendMouseFrame(command, button = -1) {
  const queue = mouseState.frames;
  // Replace stale cursor positions, but keep every click and button transition in order.
  const replaceable = button === -1 || button === 0 || button === 1;
  if (replaceable && queue.at(-1)?.button === button) queue.pop();
  if (queue.length >= 12) {
    disableMouseMode("Mouse Mode stopped because Windows input fell behind.");
    return;
  }
  queue.push({ command, button });
  flushMouseFrames();
}

function screenPoint(point) {
  const map = (value, low, center, high) => Math.max(0, Math.min(1, 0.5 +
    0.5 * (value - center) / (value < center ? center - low : high - center)));
  return {
    x: map(1 - point.x, ...mouseProfile.slice(0, 3)),
    y: map(point.y, ...mouseProfile.slice(3, 6)),
  };
}

function smoothPointer(target, elapsed) {
  const previous = mouseState.position;
  if (!previous) return target;
  const error = Math.hypot(target.x - previous.x, target.y - previous.y);
  // Use a stronger filter for small movements and follow large movements faster.
  const response = Math.exp((50 - mouseTuning.smoothing) * 0.018);
  const fraction = 1 - Math.exp(-elapsed * response * (0.006 + Math.min(error, 0.16) * 0.23));
  const axis = (current, next) => {
    if ((next === 0 || next === 1) && Math.abs(next - current) < 0.004) return next;
    if (error < 0.0025 / Math.sqrt(response)) return current;
    return current + fraction * (next - current);
  };
  return { x: axis(previous.x, target.x), y: axis(previous.y, target.y) };
}

function pinchRatios(hand, aspect) {
  const distance = (a, b) => Math.hypot((a.x - b.x) * aspect, a.y - b.y);
  const palm = Math.max(distance(hand[0], hand[9]), distance(hand[5], hand[17]), 0.001);
  return pinchTips.map(tip => distance(hand[4], hand[tip]) / palm);
}

function fingerExtended(hand, base, distance) {
  return distance(hand[base], hand[base + 3]) >
    0.85 * (distance(hand[base], hand[base + 1]) + distance(hand[base + 1], hand[base + 2]) +
    distance(hand[base + 2], hand[base + 3]));
}

function setMousePaused(paused) {
  mouseState.paused = paused;
  mouseState.frames.length = 0;
  mouseState.palmHold = null;
  mouseState.palmLatched = true;
  mouseState.palmReleasedAt = null;
  mouseState.keyboardPose = null;
  resetMouseGesture();
  sendMouseFrame("idle");
  mouseMessage(paused ? "Mouse is paused." : "Mouse resumed. Point to move the cursor.");
  updateMouseButton();
}

function updatePalmPause(hands, timestamp, distance) {
  const hand = hands.length === 1 ? hands[0] : null;
  const palmSize = hand ? distance(hand[0], hand[9]) : 0;
  const open = palmPauseSetting.checked && hand && palmSize > 0.015 &&
    distance(hand[5], hand[17]) > 0.42 * palmSize &&
    [5, 9, 13, 17].every(base => fingerExtended(hand, base, distance)) &&
    distance(hand[4], hand[8]) > 0.70 * palmSize;
  if (!open) {
    mouseState.palmHold = null;
    mouseState.palmReleasedAt ??= timestamp;
    if (timestamp - mouseState.palmReleasedAt >= 180) mouseState.palmLatched = false;
    if (mouseState.paused) sendMouseFrame("idle");
    return mouseState.paused;
  }
  mouseState.palmReleasedAt = null;
  if (!mouseState.palmLatched) {
    const hold = mouseState.palmHold;
    if (!hold || timestamp - hold.last > 240 || distance(hold.wrist, hand[0]) > palmSize * 0.20) {
      mouseState.palmHold = { wrist: hand[0], start: timestamp, last: timestamp };
      mouseState.frames.length = 0;
      mouseState.keyboardPose = null;
      resetMouseGesture();
    } else {
      hold.last = timestamp;
      if (timestamp - hold.start >= 750) {
        setMousePaused(!mouseState.paused);
        return true;
      }
    }
    mouseMessage(mouseState.paused ? "Keep your palm still to resume." : "Keep your palm still to pause.");
  }
  sendMouseFrame("idle");
  return true;
}

function updateKeyboardGesture(hands, timestamp, distance) {
  const [first, second] = hands;
  const touching = first && second && fingerExtended(first, 5, distance) && fingerExtended(second, 5, distance) &&
    distance(first[8], second[8]) < 0.32 * Math.min(distance(first[0], first[9]), distance(second[0], second[9]));
  const previous = mouseState.keyboardPose;
  if (!touching) {
    if (previous && timestamp - previous.lastSeen > 180) mouseState.keyboardPose = null;
    // An isolated lost frame should neither trigger the opener nor send a mouse click.
    if (mouseState.keyboardPose && !second) {
      if (!practice) sendMouseFrame("idle");
      return true;
    }
    return false;
  }
  if (mouseState.pinch?.dragging) return false;
  const pose = mouseState.keyboardPose ??= { started: timestamp, lastSeen: timestamp, frames: 0, opened: false };
  if (timestamp - pose.lastSeen > 180) {
    pose.started = timestamp;
    pose.frames = 0;
    pose.opened = false;
  }
  pose.lastSeen = timestamp;
  pose.frames++;
  resetMouseGesture();
  if (!pose.opened && timestamp - pose.started >= 500 && pose.frames >= 4) {
    pose.opened = true;
    if (practice) {
      openPracticeKeyboard();
      reportPracticeGesture("Keyboard opened");
    }
    else sendMouseFrame("keyboard", 7);
  } else if (!practice) {
    sendMouseFrame("idle");
  }
  mouseMessage(pose.opened ? "Keyboard opened. Use one hand to point and pinch." : "Hold your index fingertips together to open the keyboard.");
  return true;
}

function scrollGesture(hand, ratios, distance, timestamp) {
  const extended = base => fingerExtended(hand, base, distance);
  const pose = extended(5) && extended(9) && !extended(13) && !extended(17) &&
    distance(hand[8], hand[12]) > 0.27 * distance(hand[0], hand[9]) &&
    ratios.every((ratio, finger) => ratio >= mouseProfile[6 + finger] + 0.1);
  if (!pose && !mouseState.scroll) return false;
  if (!pose) {
    mouseState.scroll = null;
    return false;
  }
  const y = hand[0].y;
  const scroll = mouseState.scroll ??= { since: timestamp, anchor: y };
  const ready = timestamp - scroll.since >= 170;
  let action = 0;
  if (!ready) {
    scroll.anchor = y;
  } else if (Math.abs(y - scroll.anchor) >= 0.024 * Math.exp((50 - mouseTuning.scrollSpeed) * 0.022)) {
    action = y < scroll.anchor ? 5 : 6;
    scroll.anchor = y;
  }
  mouseMessage(ready ? "Scrolling · move up or down." : "Hold the two-finger pose to scroll.");
  mouseState.position ??= screenPoint(hand[8]);
  if (practice) {
    if (action) reportPracticeGesture(action === 5 ? "Scroll up" : "Scroll down");
    practiceFrame(null, action);
  } else {
    const { x, y: cursorY } = mouseState.position;
    sendMouseFrame(x.toFixed(4) + " " + cursorY.toFixed(4) + " " + action, action);
  }
  return true;
}

function updateMouseFrame(hands, timestamp, aspect) {
  if (calibration) {
    sampleCalibration(hands.length === 1 ? hands[0] : null, timestamp, aspect);
    return;
  }
  if (!mouseState.active && !practice) return;
  const distance = (a, b) => Math.hypot((a.x - b.x) * aspect, a.y - b.y);
  if (mouseState.active && updatePalmPause(hands, timestamp, distance)) return;
  if (practice?.kind === "typing" && !practice.keyboardOpen) {
    updateKeyboardGesture(hands, timestamp, distance);
    return;
  }
  if (mouseState.active && keyboardFeature.checked && updateKeyboardGesture(hands, timestamp, distance)) return;
  const wrist = mouseState.wrist;
  const hand = wrist
    ? hands.reduce((best, candidate) =>
      !best || distance(candidate[0], wrist) < distance(best[0], wrist) ? candidate : best, null)
    : hands[0];
  if (!hand || (wrist && distance(hand[0], wrist) > 0.22)) {
    resetMouseGesture();
    mouseMessage("Paused. Show one hand to resume.");
    if (practice) practiceFrame(null, 0);
    else sendMouseFrame("idle");
    return;
  }

  mouseState.wrist = hand[0];
  const elapsed = Math.min(80, Math.max(8, timestamp - (mouseState.lastSample ?? timestamp - 33)));
  mouseState.lastSample = timestamp;
  const ratios = pinchRatios(hand, aspect);
  const calibrated = mouseProfile.slice(6);
  const sensitivity = (mouseTuning.sensitivity - 50) * 0.0025;
  const close = calibrated.map(value => Math.max(0.18, Math.min(0.82, value + sensitivity)));
  const open = close.map((value, finger) => Math.max(value + 0.045, calibrated[finger] + 0.1 + sensitivity * 0.35));
  ratios.forEach((ratio, finger) => {
    if (ratio >= open[finger]) mouseState.armed[finger] = true;
  });
  if (practice?.kind === "scroll") {
    if (!scrollGesture(hand, ratios, distance, timestamp)) {
      mouseMessage("Raise your index and middle fingers. Curl the other fingers.");
    }
    return;
  }
  if (!practice && !mouseState.pinch && scrollGesture(hand, ratios, distance, timestamp)) return;

  const target = screenPoint(hand[8]);
  const recent = mouseState.lastLeftClick;
  const sameClickTarget = recent && timestamp - recent.time <= 440 &&
    Math.hypot(target.x - recent.position.x, target.y - recent.position.y) < 0.035;
  if (!sameClickTarget && !mouseState.pinch) mouseState.lastLeftClick = null;
  const closest = ratios.reduce((best, ratio, finger) =>
    mouseState.armed[finger] && ratio <= close[finger] && (best === -1 || ratio < ratios[best]) ? finger : best, -1);
  let pinch = mouseState.pinch;
  if (!pinch && closest !== -1) {
    mouseState.pinch = pinch = {
      finger: closest,
      started: timestamp,
      confirmed: ratios[closest] <= close[closest] - 0.08,
      dragging: false,
      origin: screenPoint(hand[9]),
      anchor: closest === 0 && sameClickTarget ? recent.position : (mouseState.position ?? target),
    };
  } else if (pinch && !pinch.dragging && closest !== -1 && closest !== pinch.finger &&
             ratios[closest] + 0.14 < ratios[pinch.finger]) {
    // A neighboring finger can briefly appear closer as the intended pinch begins.
    pinch.finger = closest;
    pinch.started = timestamp;
    pinch.confirmed = ratios[closest] <= close[closest] - 0.08;
  }

  const released = pinch && ratios[pinch.finger] >= open[pinch.finger];
  let button = 0;
  if (released) {
    if (pinch.dragging) {
      mouseState.lastLeftClick = null;
    } else if (pinch.confirmed) {
      button = pinch.finger + 2;
      mouseState.lastLeftClick = button === 2 ? { time: timestamp, position: pinch.anchor } : null;
    }
    if (!pinch.dragging) mouseState.position = pinch.anchor;
    mouseState.pinch = pinch = null;
    mouseState.armed = ratios.map((ratio, finger) => ratio >= open[finger]);
  } else if (pinch && timestamp > pinch.started && ratios[pinch.finger] <= close[pinch.finger]) {
    pinch.confirmed = true;
  }

  if (pinch?.finger === 0 && pinch.confirmed && !pinch.dragging && timestamp - pinch.started >= dragDelay) {
    pinch.origin = screenPoint(hand[9]);
    pinch.dragging = true;
  }
  if (pinch?.dragging) {
    button = 1;
    const palmPosition = screenPoint(hand[9]);
    const clamp = (value) => Math.max(0, Math.min(1, value));
    mouseState.position = smoothPointer({
      x: clamp(pinch.anchor.x + palmPosition.x - pinch.origin.x),
      y: clamp(pinch.anchor.y + palmPosition.y - pinch.origin.y),
    }, elapsed);
  } else if (pinch) {
    mouseState.position = pinch.anchor;
  } else if (!released) {
    mouseState.position = sameClickTarget ? recent.position : smoothPointer(target, elapsed);
  }
  const labels = ["Left click", "Right click", "Double-click"];
  mouseMessage(pinch?.dragging ? "Dragging. Release to drop."
    : pinch ? labels[pinch.finger] + " ready. Release to click."
    : button >= 2 ? labels[button - 2] + " detected."
    : practice?.kind === "typing" ? "Aim at a key and pinch to type."
    : practice ? "Aim at the target and use its gesture." : "Mouse Mode is on. Esc to exit.");
  const position = mouseState.position;
  if (practice) {
    if (button) reportPracticeGesture(button === 1 ? "Drag" : labels[button - 2]);
    else if (released && practice.previousAction === 1) reportPracticeGesture("Drop");
    practiceFrame(position, button);
  }
  else sendMouseFrame(position.x.toFixed(4) + " " + position.y.toFixed(4) + " " + button, button);
}

mouseButton.addEventListener("click", () => mouseState.active ? disableMouseMode() : enableMouseMode());
pauseMouseButton.addEventListener("click", () => {
  if (mouseState.active) setMousePaused(!mouseState.paused);
});
keyboardFeature.addEventListener("change", () => {
  mouseState.keyboardPose = null;
  if (keyboardFeature.checked || !mouseState.active) return;
  // Drop any opening request that has not been sent. Close after the in-flight request.
  mouseState.frames = mouseState.frames.filter(frame => frame.command !== "keyboard");
  mouseState.frames.unshift({ command: "keyboard-close", button: -2 });
  flushMouseFrames();
});
document.addEventListener("keydown", (event) => {
  if (event.key === "Escape") disableMouseMode();
});
window.addEventListener("pagehide", () => {
  if (mouseState.active || mouseState.changing) navigator.sendBeacon("mouse", "disable");
});
