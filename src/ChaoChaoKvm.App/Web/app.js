const $ = (s) => document.querySelector(s);
const state = { hostRunning:false, statusText:'待命', fps:18, quality:65 };

function send(action, extra = {}) {
  if (window.chrome?.webview) {
    window.chrome.webview.postMessage({ action, ...extra });
  } else {
    console.log('[bridge preview]', action, extra);
  }
}

function render(s) {
  Object.assign(state, s || {});
  document.title = `ChaoChao KVM · ${state.statusText || '待命'}`;
  $('#statusText').textContent = state.statusText || '待命';
  $('#engineText').textContent = state.hostRunning ? 'HOST ONLINE' : 'STANDBY';
  $('#hostSwitch').classList.toggle('on', !!state.hostRunning);
  $('#hostBtn').textContent = state.hostRunning ? '停止分享本機' : '開始分享本機';
  if (state.fps) $('#fpsValue').textContent = state.fps;
  if (state.quality) $('#qualityValue').textContent = state.quality;
}

window.chrome?.webview?.addEventListener('message', (event) => {
  const message = event.data;
  if (message?.type === 'app.state') render(message.state);
  if (message?.type === 'bridge.error') $('#statusText').textContent = `Bridge: ${message.message}`;
});

$('#hostBtn').addEventListener('click', () => send(state.hostRunning ? 'host.stop' : 'host.start'));
$('#hostSwitch').addEventListener('click', () => send(state.hostRunning ? 'host.stop' : 'host.start'));
$('#connectBtn').addEventListener('click', () => {
  const ip = $('#targetIp').value.trim();
  const port = Number($('#port').value) || 45888;
  send('target.set', { ip, port });
  $('#connectBtn').animate([{transform:'translateY(0)'},{transform:'translateY(-2px)'},{transform:'translateY(0)'}],{duration:220});
});

document.querySelectorAll('.nav').forEach(btn => btn.addEventListener('click', () => {
  document.querySelectorAll('.nav').forEach(x => x.classList.remove('active'));
  btn.classList.add('active');
}));

send('app.getState');
render(state);
