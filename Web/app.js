(() => {
  'use strict';
  const $ = id => document.getElementById(id);
  const bridge = window.chrome && window.chrome.webview;
  let state = { connected: false, peer: '', allowControl: false, library:{groups:[],devices:[]}, sessions:[] }, busy = false, busyTimer, toastTimer, addressList = null, deviceGroup = '__all';
  let groupRenderSignature = '', deviceRenderSignature = '', sessionRenderSignature = '';
  const send = (command, payload = {}) => {
    if (!bridge) { toast('這是介面預覽，請從吵吵 KVM 桌面程式操作。'); return false; }
    bridge.postMessage({ command, ...payload }); return true;
  };
  function setTextSize(size, persist = true) {
    const allowed = ['standard','comfortable','large'];
    if (!allowed.includes(size)) size = 'comfortable';
    const host = document.body || document.documentElement;
    if (host && host.dataset) host.dataset.textSize = size;
    document.querySelectorAll('[data-text-size]').forEach(button => {
      const active = button.dataset.textSize === size;
      button.classList.toggle('active', active);
      button.setAttribute('aria-pressed', String(active));
    });
    if (persist) { try { localStorage.setItem('chao-kvm-text-size', size); } catch (_) {} }
  }
  let initialTextSize = 'comfortable';
  try { initialTextSize = localStorage.getItem('chao-kvm-text-size') || 'comfortable'; } catch (_) {}
  setTextSize(initialTextSize, false);
  document.querySelectorAll('[data-text-size]').forEach(button => button.addEventListener('click', () => setTextSize(button.dataset.textSize)));
  function toast(message) { $('toast').textContent = message; $('toast').hidden = false; clearTimeout(toastTimer); toastTimer = setTimeout(() => $('toast').hidden = true, 3600); }
  function page(name) {
    document.querySelectorAll('.page').forEach(el => el.hidden = el.id !== 'page-' + name);
    document.querySelectorAll('.nav').forEach(el => { const active = el.dataset.page === name; el.classList.toggle('active', active); if (active) el.setAttribute('aria-current', 'page'); else el.removeAttribute('aria-current'); });
    $('breadcrumb').textContent = '工作空間 / ' + ({devices:'裝置中心', online:'Online 連線', cloudflare:'VPN 443 中繼', connect:'遠端連線', files:'檔案傳輸', settings:'偏好設定'}[name]);
  }
  document.querySelectorAll('[data-page]').forEach(el => el.addEventListener('click', () => page(el.dataset.page)));
  function setBusy(value) { busy = value; $('connect-button').disabled = value; $('connect-button').textContent = value ? '連線中…' : '連線 ↗'; $('connect-form').setAttribute('aria-busy', String(value)); }
  $('connect-form').addEventListener('submit', e => {
    e.preventDefault(); if (busy) return;
    const ip = $('peer-ip').value.trim();
    const parts = ip.split('.');
    if (parts.length !== 4 || parts.some(p => !/^\d{1,3}$/.test(p) || Number(p) > 255)) { $('connect-error').textContent = '請輸入有效的 IPv4 位址，例如 192.168.1.20。'; $('peer-ip').setAttribute('aria-invalid','true'); $('peer-ip').focus(); return; }
    $('peer-ip').removeAttribute('aria-invalid'); $('connect-error').textContent = '';
    if (!send('connect', { ip })) return;
    setBusy(true); clearTimeout(busyTimer); busyTimer = setTimeout(() => { setBusy(false); if (!state.connected) $('connect-error').textContent = '尚未連上。請確認對方已開啟程式，位址與網路可通。背景會繼續重試。'; }, 10000);
  });
  $('open-session').addEventListener('click', () => { if (state.connected) send('openSession'); else { $('peer-ip').value = state.peer; $('connect-form').requestSubmit(); } });
  $('copy-ip').addEventListener('click', () => send('copyIp', {ip:$('local-ip').value}));
  $('allow-control').addEventListener('change', e => { if (!send('allowControl', {enabled:e.target.checked})) e.target.checked = state.allowControl; });
  $('send-files').addEventListener('click', () => send('sendFiles'));
  $('send-folder').addEventListener('click', () => send('sendFolder'));
  $('open-received').addEventListener('click', () => send('openReceived'));
  $('retry').addEventListener('click', () => send('retry'));
  $('online-copy-code').addEventListener('click', () => send('onlineCopyCode'));
  $('online-restart').addEventListener('click', () => send('onlineRestart'));
  $('online-stop').addEventListener('click', () => send('onlineStop'));
  $('online-connect-button').addEventListener('click', () => {
    const code=$('online-peer-code').value.trim();
    if(!code.startsWith('CKO2.')&&!code.startsWith('CKO1.')) { $('online-connect-error').textContent='請貼上以 CKO2. 開頭的 Online 連線碼（舊 CKO1 仍相容）。'; $('online-peer-code').focus(); return; }
    $('online-connect-error').textContent=''; send('onlineConnect',{code});
  });
  $('cf-relay-save').addEventListener('click', () => send('cloudflareRelaySave',{hostname:$('cf-relay-hostname').value.trim()}));
  $('cf-relay-start').addEventListener('click', () => send('cloudflareRelayStart',{hostname:$('cf-relay-hostname').value.trim()}));
  $('cf-relay-stop').addEventListener('click', () => send('cloudflareRelayStop'));
  $('cf-relay-stop-clients').addEventListener('click', () => send('cloudflareRelayStopClients'));
  $('cf-relay-copy').addEventListener('click', () => send('cloudflareRelayCopyCode'));
  $('cf-relay-connect').addEventListener('click', () => {
    const code=$('cf-relay-peer-code').value.trim();
    if(!code.startsWith('CCF2.')) { $('cf-relay-connect-error').textContent='請貼上以 CCF2. 開頭的 VPN 443 中繼連線碼。'; $('cf-relay-peer-code').focus(); return; }
    $('cf-relay-connect-error').textContent=''; send('cloudflareConnect',{code});
  });
  $('show-device-editor').addEventListener('click', () => openDeviceEditor());
  $('close-device-editor').addEventListener('click', () => $('device-editor').hidden = true);
  $('device-search').addEventListener('input', renderDeviceCenter);
  $('group-form').addEventListener('submit', e => { e.preventDefault(); const name=$('group-name').value.trim(); if (!name) return; if (send('addGroup',{name})) { $('group-name').value=''; } });
  $('device-form').addEventListener('submit', e => {
    e.preventDefault();
    const mode=$('device-mode').value||'lan';
    if(mode==='online') {
      if (send('updateDeviceMetadata',{id:$('device-id').value,name:$('device-name').value.trim(),group:$('device-group').value,tags:$('device-tags').value.trim(),favorite:$('device-favorite').checked})) $('device-editor').hidden=true;
      return;
    }
    const ip=$('device-ip').value.trim(), parts=ip.split('.');
    if (parts.length!==4 || parts.some(p=>!/^\d{1,3}$/.test(p)||Number(p)>255)) { toast('請輸入有效的 IPv4 位址。'); $('device-ip').focus(); return; }
    if (send('saveDevice',{id:$('device-id').value,name:$('device-name').value.trim(),ip,group:$('device-group').value,tags:$('device-tags').value.trim(),favorite:$('device-favorite').checked})) $('device-editor').hidden=true;
  });
  $('delete-device').addEventListener('click', () => { const id=$('device-id').value; if (id && send('deleteDevice',{id})) $('device-editor').hidden=true; });
  for (const [id, suffix] of [['fps',' FPS'],['quality','%']]) { $(id).addEventListener('input', () => $(id+'-value').textContent = $(id).value + suffix); $(id).addEventListener('change', () => send('quality',{fps:Number($('fps').value),quality:Number($('quality').value)})); }
  function openDeviceEditor(device) {
    const groups=(state.library&&state.library.groups)||['未分類'];
    $('device-group').replaceChildren(...groups.map(group=>{const option=document.createElement('option');option.value=group;option.textContent=group;return option;}));
    const mode=device&&device.mode==='online'?'online':'lan', address=mode==='online'?(device.endpoint||''):(device&&device.ip||'');
    $('device-id').value=device&&device.id||''; $('device-mode').value=mode; $('device-name').value=device&&device.name||''; $('device-ip').value=address; $('device-tags').value=device&&device.tags?(device.tags||[]).join(', '):''; $('device-favorite').checked=!!(device&&device.favorite);
    $('device-ip').disabled=mode==='online'; $('device-ip').required=mode!=='online'; $('device-address-label').textContent=mode==='online'?'Online endpoint':'IP 位址';
    $('device-group').value=device&&device.group||groups[0]||'未分類'; $('device-editor-title').textContent=device?'編輯裝置':'新增裝置'; $('delete-device').hidden=!device; $('device-editor').hidden=false; $('device-name').focus();
  }
  function selectDeviceGroup(group) { deviceGroup=group; document.querySelectorAll('.group-item').forEach(b=>b.classList.toggle('active',b.dataset.group===group)); renderDeviceCenter(); }
  function renderDeviceCenter() {
    const library=state.library||{groups:[],devices:[]}, devices=library.devices||[], sessions=state.sessions||[], search=$('device-search').value.trim().toLowerCase();
    $('device-count').textContent=devices.length+' 台';
    const nextGroupSignature=JSON.stringify((library.groups||[]).map(group=>[group,devices.filter(d=>d.group===group).length]));
    if(nextGroupSignature!==groupRenderSignature){
      groupRenderSignature=nextGroupSignature;
      const groupList=$('group-list'); groupList.replaceChildren();
      for (const group of (library.groups||[])) { const shell=document.createElement('div'); shell.className='group-item-shell'; const b=document.createElement('button'); b.className='group-item'; b.dataset.group=group; b.textContent=group+'  '+devices.filter(d=>d.group===group).length; b.classList.toggle('active',deviceGroup===group); b.addEventListener('click',()=>selectDeviceGroup(group)); shell.appendChild(b); if(group!=='未分類'){const del=document.createElement('button');del.className='group-delete';del.textContent='×';del.title='刪除群組';del.addEventListener('click',()=>{if(confirm('刪除群組「'+group+'」？其中裝置會移到「未分類」。')){if(deviceGroup===group)deviceGroup='__all';send('deleteGroup',{name:group});}});shell.appendChild(del);} groupList.appendChild(shell); }
    }
    document.querySelectorAll('.group-item[data-group="__all"],.group-item[data-group="__favorite"]').forEach(b=>b.classList.toggle('active',b.dataset.group===deviceGroup));
    $('device-filter-title').textContent=deviceGroup==='__all'?'全部裝置':deviceGroup==='__favorite'?'我的最愛':deviceGroup;
    let filtered=devices.filter(d=>deviceGroup==='__all'||(deviceGroup==='__favorite'&&d.favorite)||d.group===deviceGroup);
    if (search) filtered=filtered.filter(d=>[d.name,d.ip,d.endpoint,d.mode,d.group,...(d.tags||[])].join(' ').toLowerCase().includes(search));
    const nextDeviceSignature=JSON.stringify({group:deviceGroup,search,devices:filtered,sessions});
    if(nextDeviceSignature!==deviceRenderSignature){
      deviceRenderSignature=nextDeviceSignature;
      const grid=$('device-grid'); grid.replaceChildren(); $('device-empty').hidden=filtered.length>0;
      for (const device of filtered) {
        const address=device.mode==='online'?(device.endpoint||''):(device.ip||''), session=sessions.find(s=>s.ip===address), card=document.createElement('article'); card.className='managed-device'+(session&&session.active?' active-session':'')+(device.mode==='online'?' online-device':'');
        const head=document.createElement('div'); head.className='managed-device-head'; const glyph=document.createElement('span'); glyph.className='managed-device-glyph'; glyph.setAttribute('aria-hidden','true');
        const title=document.createElement('div'); title.className='managed-device-title'; const strong=document.createElement('strong'); strong.textContent=device.name||address; const ip=document.createElement('span'); ip.textContent=address; title.append(strong,ip);
        const fav=document.createElement('button'); fav.className='favorite-button'; fav.textContent=device.favorite?'★':'☆'; fav.title=device.favorite?'取消收藏':'加入收藏'; fav.addEventListener('click',()=>send('toggleFavorite',{id:device.id})); head.append(glyph,title,fav);
        const meta=document.createElement('div'); meta.className='managed-device-meta'; const status=document.createElement('span'); status.className='device-online-state'+(session&&session.connected?' online':''); status.textContent=session&&session.connected?'● 已連線':session?'● 連線中':'○ 待命'; const transport=document.createElement('span'); transport.className='device-transport'+(device.mode==='online'?' online':''); transport.textContent=device.mode==='online'?'ONLINE':'LAN'; const group=document.createElement('span'); group.textContent=device.group||'未分類'; meta.append(status,transport,group);
        const tags=document.createElement('div'); tags.className='device-tags'; for(const tag of (device.tags||[])){const chip=document.createElement('span');chip.textContent=tag;tags.appendChild(chip);}
        const actions=document.createElement('div'); actions.className='managed-device-actions'; const open=document.createElement('button'); open.className='primary compact'; open.textContent=session?'切到分頁':'開啟遠端'; open.addEventListener('click',()=>session?send('selectSession',{ip:session.ip}):send('openDeviceSession',{id:device.id})); const edit=document.createElement('button'); edit.className='secondary-button compact'; edit.textContent='編輯'; edit.addEventListener('click',()=>openDeviceEditor(device)); actions.append(edit,open);
        card.addEventListener('dblclick',()=>session?send('selectSession',{ip:session.ip}):send('openDeviceSession',{id:device.id}));
        card.append(head,meta,tags,actions); grid.appendChild(card);
      }
    }
    const nextSessionSignature=JSON.stringify(sessions);
    if(nextSessionSignature!==sessionRenderSignature){
      sessionRenderSignature=nextSessionSignature;
      const strip=$('session-strip'); strip.hidden=!sessions.length; strip.replaceChildren();
      if(sessions.length){const label=document.createElement('span');label.className='session-strip-label';label.textContent='工作階段';strip.appendChild(label);for(const s of sessions){const tab=document.createElement('button');tab.className='session-chip'+(s.active?' active':'');tab.textContent=(s.connected?'● ':'○ ')+(s.name||s.ip);tab.addEventListener('click',()=>send('selectSession',{ip:s.ip}));const close=document.createElement('button');close.className='session-close';close.textContent='×';close.title='關閉分頁';close.addEventListener('click',()=>send('closeSession',{ip:s.ip}));const shell=document.createElement('span');shell.className='session-chip-shell';shell.append(tab,close);strip.appendChild(shell);}}
    }
  }
  function render(next) {
    state = next;
    $('nav-online').hidden = !next.onlineMode;
    $('nav-cloudflare').hidden = !next.cloudflareMode;
    const relay=next.cloudflareRelay||{};
    if(document.activeElement!==$('cf-relay-hostname')) $('cf-relay-hostname').value=relay.hostname||'kvm-relay.chaochao.win';
    $('cf-relay-status').textContent=relay.detail||'Cloudflare HTTPS 中繼尚未啟動';
    $('cf-relay-clients').textContent=relay.connected?'已連上 Cloudflare · '+(relay.clients||0)+' 個本機連線':'目前 '+(relay.clients||0)+' 個本機連線';
    $('cf-relay-code').value=relay.pairingCode||'';
    $('cf-relay-dot').classList.toggle('online',Boolean(relay.connected));
    $('cf-relay-copy').disabled=!relay.pairingCode;
    $('cf-relay-stop').disabled=!relay.running;
    const online=next.online||{};
    $('online-status').textContent=online.detail||'ngrok 尚未啟動';
    $('online-url').textContent=online.publicUrl||'尚未取得公開網址';
    $('online-local-code').value=online.pairingCode||'';
    $('online-copy-code').disabled=!online.pairingCode;
    $('online-stop').disabled=!online.running;
    $('online-dot').classList.toggle('online',Boolean(online.publicUrl));
    $('online-port-note').textContent='Online KVM 本機服務 Port '+(online.listenPort||18001)+'，不影響 LAN 版 HTTP 8001。';
    $('machine').textContent = $('sidebar-machine').textContent = next.machine || '本機電腦';
    const addresses = next.localIps || (next.localIp ? [next.localIp] : []);
    if (addressList !== JSON.stringify(addresses)) {
      addressList = JSON.stringify(addresses); const selected = $('local-ip').value;
      $('local-ip').replaceChildren(...(addresses.length ? addresses : ['']).map(ip => { const option = document.createElement('option'); option.value = ip; option.textContent = ip || '未偵測到位址'; return option; }));
      $('local-ip').value = addresses.includes(selected) ? selected : addresses[0] || '';
    }
    $('copy-ip').disabled = !addresses.length;
    const label = next.connected ? '已連線' : next.running ? '可供連線' : '尚未啟動';
    $('status-label').textContent = $('sidebar-status').textContent = label;
    $('status-dot').classList.toggle('online', Boolean(next.connected));
    $('allow-control').checked = !!next.allowControl;
    if (document.activeElement !== $('peer-ip') && !$('peer-ip').value) $('peer-ip').value = next.peer || '';
    $('empty-peer').hidden = !!next.peer; $('peer-row').hidden = !next.peer;
    $('peer-name').textContent = next.peerName || '遠端電腦'; $('peer-address').textContent = next.peer || '';
    $('peer-state').textContent = next.connected ? '連線可用' : '尚未連上';
    $('peer-dot').classList.toggle('online', Boolean(next.connected));
    $('open-session').textContent = next.connected ? '開啟遠端桌面 ↗' : '重新連線 ↗';
    $('send-files').disabled = $('send-folder').disabled = !next.connected || !!next.transferring;
    $('file-peer').textContent = next.connected ? '傳送至 ' + (next.peerName || next.peer) : '連線後即可傳送檔案。';
    $('receive-path').textContent = next.receiveDirectory || '檔案會儲存至本機收件資料夾。';
    $('transfer-status').textContent = next.transferStatus || '尚無進行中的傳輸';
    for (const [id,value,suffix] of [['fps',next.fps,' FPS'],['quality',next.quality,'%']]) { if (document.activeElement !== $(id)) { $(id).value = value || (id === 'fps' ? 10 : 55); $(id+'-value').textContent = $(id).value + suffix; } }
    $('route').textContent = next.route || '等待連線'; $('agent-api').textContent = next.agentApi || '—';
    $('fatal').hidden = !next.error; $('fatal').textContent = next.error || '';
    renderDeviceCenter();
    if (next.connected && busy) { clearTimeout(busyTimer); setBusy(false); $('connect-error').textContent = ''; }
  }
  if (bridge) { bridge.addEventListener('message', e => { const m = e.data; if (m.type === 'state') render(m.state); else if (m.type === 'toast') toast(m.message); else if (m.type === 'connectError') { clearTimeout(busyTimer); setBusy(false); $('connect-error').textContent = m.message; } else if (m.type === 'logs') $('logs').textContent = m.text || '尚無紀錄'; else if (m.type === 'page') page(m.name); }); send('ready'); }
  else { $('preview-banner').hidden = false; render({machine:'本機電腦',localIp:'',peer:'',running:false,connected:false,allowControl:false,fps:10,quality:55,library:{groups:['未分類','公司','家用'],devices:[]},sessions:[],onlineMode:false,online:{}}); }
})();
