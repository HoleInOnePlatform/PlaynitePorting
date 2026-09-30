import { isRestSiteSignal, forwardRestSiteSignal } from './signal.js';

const $ = id => document.getElementById(id);
const status = $('status');
const log = $('log');
let stream;
let received = false;
function report(message) {
  status.textContent = message;
  log.textContent = `${new Date().toLocaleTimeString()}  ${message}\n` + log.textContent;
}
function onApplicationMessage(message) {
  if (!isRestSiteSignal(message)) return;
  if (received) return;
  received = true;
  $('rest-indicator').classList.add('reached');
  $('rest-label').textContent = '모닥불 도달 확인';
  report('모닥불 신호 수신 · 스트림은 계속 실행 중');
  forwardRestSiteSignal(message);
}

try {
  const response = await fetch('/api/options');
  const options = await response.json();
  for (const option of options) $('application').add(new Option(option.label, option.index));
  report(options.length ? '스트림을 시작할 준비가 되었습니다' : '서버에 STREAM_OPTIONS를 설정하세요');
} catch { report('서버 설정을 불러오지 못했습니다'); }

$('start').addEventListener('click', async () => {
  if (stream) return;
  $('start').disabled = true;
  received = false;
  try {
    const { GameLiftStreams } = await import('./vendor/gameliftstreams.mjs');
    stream = new GameLiftStreams({
      videoElement: $('video'),
      audioElement: $('audio'),
      clientConnection: {
        applicationMessage: onApplicationMessage,
        connectionState: state => report(`연결 상태: ${String(state)}`),
        channelError: error => report(`채널 오류: ${String(error)}`),
        serverDisconnect: () => report('스트림 연결이 끊어졌습니다')
      },
      inputConfiguration: { autoKeyboard: true, autoMouse: true, autoGamepad: true }
    });
    report('스트림 세션 요청 중');
    const signalRequest = await stream.generateSignalRequest();
    const response = await fetch('/api/streams', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ index: Number($('application').value), signalRequest })
    });
    if (!response.ok) throw new Error((await response.json()).error);
    const { signalResponse } = await response.json();
    await stream.processSignalResponse(signalResponse);
    stream.attachInput();
    $('stage').classList.add('playing');
    $('stop').disabled = false;
    report('스트림 연결됨 · 입력 연결됨');
  } catch (error) {
    report(`연결 실패: ${error.message}`);
    stream?.close(); stream = undefined;
    $('start').disabled = false;
  }
});

$('stop').addEventListener('click', () => {
  stream?.close(); stream = undefined;
  $('stage').classList.remove('playing');
  $('start').disabled = false; $('stop').disabled = true;
  report('브라우저 연결 종료');
});
