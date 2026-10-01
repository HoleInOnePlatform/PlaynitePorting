// Video, audio and input only. Handoff commands are subscribed by the native C# service.
const video = document.getElementById('video');
const audio = document.getElementById('audio');
const status = document.getElementById('status');
const ticket = new URLSearchParams(location.hash.slice(1)).get('ticket');
history.replaceState(null, '', location.pathname);
let stream;
try {
  if (!ticket) throw new Error('A new native stream ticket is required.');
  const { GameLiftStreams } = await import('./vendor/gameliftstreams.mjs');
  stream = new GameLiftStreams({
    videoElement: video, audioElement: audio,
    clientConnection: {
      connectionState: state => { status.textContent = state; },
      channelError: () => { status.textContent = '연결 오류. 홀인원 창에서 연결을 다시 시도하세요.'; },
      serverDisconnect: () => { status.textContent = '스트림 연결이 종료되었습니다.'; }
    },
    inputConfiguration: { autoKeyboard: true, autoMouse: true, autoGamepad: true }
  });
  const signalRequest = await stream.generateSignalRequest();
  const response = await fetch('/v2/stream/connect', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${ticket}` },
    body: JSON.stringify({ signalRequest }), redirect: 'error', cache: 'no-store'
  });
  if (!response.ok) throw new Error(`Stream connection rejected (${response.status}).`);
  const { signalResponse } = await response.json();
  await stream.processSignalResponse(signalResponse);
  stream.attachInput();
  status.textContent = '';
} catch {
  status.textContent = '스트림 연결을 시작할 수 없습니다. 홀인원 창에서 연결을 다시 시도하세요.';
  stream?.close(); stream = undefined;
}
window.addEventListener('pagehide', () => stream?.close(), { once: true });
