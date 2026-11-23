"use strict";

const visionUrl = "https://cdn.jsdelivr.net/npm/@mediapipe/tasks-vision@0.10.21";
const modelUrl = "https://storage.googleapis.com/mediapipe-models/hand_landmarker/hand_landmarker/float16/1/hand_landmarker.task";
// Center each control on its fingertip in the hand drawing's 32 × 32 viewBox.
const handViewBoxSize = 32;
const fingers = [
  { name: "Thumb", landmark: 4, color: "#faaa93", x: 2.75, y: 27.05 },
  { name: "Index", landmark: 8, color: "#89edb4", x: 7.1, y: 7.95 },
  { name: "Middle", landmark: 12, color: "#85daff", x: 14.55, y: 2.65 },
  { name: "Ring", landmark: 16, color: "#d6b6ff", x: 22.07, y: 3.27 },
  { name: "Pinky", landmark: 20, color: "#ffda88", x: 29.43, y: 8.66 },
];

const cameraButton = document.querySelector("#camera");
const fingerControls = document.querySelector("#fingers");
const feed = document.querySelector("#feed");
const stage = feed.parentElement;
const video = document.querySelector("#video");
const canvas = document.querySelector("#markers");
const context = canvas.getContext("2d");
const trackingImage = document.createElement("canvas");
const trackingContext = trackingImage.getContext("2d");
const placeholder = document.querySelector("#placeholder");
const message = document.querySelector("#message");
const cameraStatus = document.querySelector("#status");
const choices = fingers.map(({ name, color, x, y }) => {
  const button = document.createElement("button");
  button.className = "finger";
  button.type = "button";
  button.title = name;
  button.setAttribute("aria-label", name);
  button.setAttribute("aria-pressed", "true");
  button.style.setProperty("--finger-color", color);
  button.style.setProperty("--finger-x", (x / handViewBoxSize) * 100 + "%");
  button.style.setProperty("--finger-y", (y / handViewBoxSize) * 100 + "%");
  button.addEventListener("click", () => {
    button.setAttribute("aria-pressed", String(button.getAttribute("aria-pressed") !== "true"));
    drawMarkers();
    savePreferences();
  });
  fingerControls.append(button);
  return button;
});

let tracker;
let stream;
let animation;
let videoCallback;
let trackedHandLimit = 2;
let trackingOptions;
let hands = [];
let previousFrame = -1;
let starting = false;
let closing = false;

function setStatus(text, live = false) {
  if (cameraStatus.textContent !== text) {
    cameraStatus.textContent = text;
  }
  if (cameraStatus.dataset.live !== String(live)) cameraStatus.dataset.live = String(live);
}

function selectFingers(checked) {
  for (const choice of choices) {
    choice.setAttribute("aria-pressed", String(checked));
  }
  drawMarkers();
  savePreferences();
}

async function loadTracker() {
  const { FilesetResolver, HandLandmarker } = await import(visionUrl + "/+esm");
  const files = await FilesetResolver.forVisionTasks(visionUrl + "/wasm");
  return HandLandmarker.createFromOptions(files, {
    baseOptions: { modelAssetPath: modelUrl, delegate: "CPU" },
    runningMode: "VIDEO",
    numHands: 2,
  });
}

function fitPreview() {
  if (!video.videoWidth || !video.videoHeight) return;
  const scale = Math.min(stage.clientWidth / video.videoWidth, stage.clientHeight / video.videoHeight);
  const width = Math.max(1, Math.round(video.videoWidth * scale));
  const height = Math.max(1, Math.round(video.videoHeight * scale));
  feed.style.width = width + "px";
  feed.style.height = height + "px";
  const pixelRatio = window.devicePixelRatio || 1;
  canvas.width = Math.round(width * pixelRatio);
  canvas.height = Math.round(height * pixelRatio);
  drawMarkers();
}

function drawMarkers() {
  context.clearRect(0, 0, canvas.width, canvas.height);
  const pixelRatio = window.devicePixelRatio || 1;
  const radius = 5 * pixelRatio;
  for (const hand of hands) {
    fingers.forEach(({ landmark, color }, index) => {
      if (choices[index].getAttribute("aria-pressed") !== "true") return;
      const point = hand[landmark];
      context.beginPath();
      context.arc(point.x * canvas.width, point.y * canvas.height, radius, 0, 2 * Math.PI);
      context.fillStyle = color;
      context.fill();
      context.strokeStyle = "#101715";
      context.lineWidth = 2 * pixelRatio;
      context.stroke();
    });
  }
}

function scheduleTracking() {
  if (!stream) return;
  // A timer keeps tracking when Edge is covered or minimized and frame callbacks stall.
  const tick = (timestamp) => {
    clearTimeout(animation);
    if (videoCallback !== undefined) video.cancelVideoFrameCallback(videoCallback);
    videoCallback = undefined;
    void trackFrame(timestamp);
  };
  if (video.requestVideoFrameCallback) videoCallback = video.requestVideoFrameCallback(tick);
  animation = setTimeout(() => tick(performance.now()), document.hidden ? 30 : 48);
}

async function trackFrame(timestamp) {
  const activeStream = stream;
  const activeTracker = tracker;
  if (!activeStream) return;
  try {
    if (video.readyState >= HTMLMediaElement.HAVE_CURRENT_DATA && video.currentTime !== previousFrame) {
      const waitingForKeyboard = practice?.kind === "typing" && !practice.keyboardOpen;
      const singleHand = (practice && !waitingForKeyboard) || calibration || (mouseState.active && !keyboardFeature.checked);
      const wanted = singleHand ? 1 : 2;
      if (wanted !== trackedHandLimit) {
        trackingOptions = activeTracker.setOptions({ numHands: wanted }).then(() => {
          trackedHandLimit = wanted;
        });
        try {
          await trackingOptions;
        } finally {
          trackingOptions = undefined;
        }
        if (stream !== activeStream) return;
      }
      previousFrame = video.currentTime;
      let source = video;
      if ((mouseState.active || practice || wanted === 1) && video.videoWidth > 360) {
        const height = Math.round(360 * video.videoHeight / video.videoWidth);
        if (trackingImage.width !== 360 || trackingImage.height !== height) {
          trackingImage.width = 360;
          trackingImage.height = height;
        }
        trackingContext.drawImage(video, 0, 0, 360, height);
        source = trackingImage;
      }
      hands = activeTracker.detectForVideo(source, Math.max(timestamp, performance.now())).landmarks;
      updateTrackingFps(performance.now());
      updateMouseFrame(hands, timestamp, video.videoWidth / video.videoHeight);
      if (!document.hidden) drawMarkers();
      const liveStatus = ["Looking for hands", "1 hand detected", "2 hands detected"];
      setStatus(liveStatus[hands.length], true);
    }
  } catch (error) {
    if (stream !== activeStream) return;
    console.error(error);
    tracker?.close();
    tracker = undefined;
    trackedHandLimit = 2;
    stopCamera("Tracking stopped. Start the camera to try again.", "Tracking error");
    return;
  }
  if (stream === activeStream) scheduleTracking();
}

function stopCamera(text = "Start your camera to track your fingertips.", state = "Camera off") {
  clearTimeout(animation);
  if (videoCallback !== undefined) video.cancelVideoFrameCallback(videoCallback);
  videoCallback = undefined;
  mouseCameraReady(false);
  const activeStream = stream;
  stream = undefined;
  for (const track of activeStream?.getTracks() ?? []) {
    track.stop();
  }
  video.srcObject = null;
  hands = [];
  previousFrame = -1;
  drawMarkers();
  feed.hidden = true;
  placeholder.hidden = false;
  message.textContent = text;
  cameraButton.textContent = "Start camera";
  setStatus(state);
}

function cameraError(error) {
  switch (error.name) {
    case "NotAllowedError":
      return "Allow camera access in Edge and Windows, then try again.";
    case "NotFoundError":
      return "No camera found. Connect or enable a webcam, then try again.";
    case "NotReadableError":
      return "The camera is busy or unavailable. Close other camera apps and try again.";
    default:
      return "The camera could not start. Check your webcam and try again.";
  }
}

async function startCamera() {
  if (starting) return;
  if (!navigator.mediaDevices?.getUserMedia) {
    stopCamera("Open the app with its Windows launcher to use your webcam.", "Camera unavailable");
    return;
  }

  starting = true;
  cameraButton.disabled = true;
  let loadingModel = false;
  try {
    setStatus("Waiting for camera");
    message.textContent = "Allow camera access if prompted.";
    const openedStream = await navigator.mediaDevices.getUserMedia({
      audio: false,
      video: { width: { ideal: 640 }, height: { ideal: 480 }, frameRate: { ideal: 30, max: 30 } },
    });
    if (closing) {
      openedStream.getTracks().forEach((track) => track.stop());
      return;
    }
    stream = openedStream;
    video.srcObject = stream;
    stream.getVideoTracks()[0].addEventListener("ended", () => {
      if (stream === openedStream) {
        stopCamera("Camera disconnected. Reconnect it and try again.", "Camera disconnected");
      }
    }, { once: true });

    await video.play();
    await trackingOptions;
    if (stream !== openedStream) return;
    if (!tracker) {
      loadingModel = true;
      setStatus("Loading hand tracker");
      message.textContent = "Loading the hand tracking model…";
      tracker = await loadTracker();
      if (closing) {
        tracker.close();
        tracker = undefined;
      }
    }
    if (stream !== openedStream) return;

    feed.hidden = false;
    placeholder.hidden = true;
    fitPreview();
    setStatus("Looking for hands", true);
    cameraButton.textContent = "Stop camera";
    mouseCameraReady(true);
    scheduleTracking();
  } catch (error) {
    console.error(error);
    const text = loadingModel
      ? "The hand tracking model could not load. Check your internet connection and try again."
      : cameraError(error);
    stopCamera(text, loadingModel ? "Tracker unavailable" : "Camera unavailable");
  } finally {
    starting = false;
    cameraButton.disabled = false;
  }
}

cameraButton.addEventListener("click", () => stream ? stopCamera() : startCamera());
document.querySelector("#select-all").addEventListener("click", () => selectFingers(true));
document.querySelector("#clear").addEventListener("click", () => selectFingers(false));
new ResizeObserver(fitPreview).observe(stage);
video.addEventListener("resize", fitPreview);
window.addEventListener("pagehide", () => {
  closing = true;
  stopCamera();
  tracker?.close();
  tracker = undefined;
});
