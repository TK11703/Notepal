// Camera capture helpers used by the CameraCapture component.
const captures = new Map();

export async function start(video) {
    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
        throw new Error('Camera access is not available in this browser (a secure https connection is required).');
    }

    stop(video);
    const stream = await navigator.mediaDevices.getUserMedia({
        audio: false,
        video: { facingMode: { ideal: 'environment' }, width: { ideal: 2560 }, height: { ideal: 1440 } }
    });
    video.srcObject = stream;
    await video.play();
}

export function stop(video) {
    const stream = video && video.srcObject;
    if (stream) {
        stream.getTracks().forEach(t => t.stop());
        video.srcObject = null;
    }
}

// Captures the current frame as a JPEG and returns an object URL usable as a thumbnail.
export async function capture(video) {
    if (!video.videoWidth) {
        throw new Error('The camera is not ready yet.');
    }

    const canvas = document.createElement('canvas');
    canvas.width = video.videoWidth;
    canvas.height = video.videoHeight;
    canvas.getContext('2d').drawImage(video, 0, 0, canvas.width, canvas.height);
    const blob = await new Promise(resolve => canvas.toBlob(resolve, 'image/jpeg', 0.9));
    const url = URL.createObjectURL(blob);
    captures.set(url, blob);
    return url;
}

// Returns the captured blob so .NET can read it as a stream (avoids SignalR message size limits).
export function getBlob(url) {
    return captures.get(url);
}

export function release(url) {
    if (captures.delete(url)) {
        URL.revokeObjectURL(url);
    }
}

export function releaseAll() {
    captures.forEach((_, url) => URL.revokeObjectURL(url));
    captures.clear();
}
