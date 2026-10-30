'use strict';
// 3D Кинотеатр: стереоплеер для DLP-Link (frame-sequential / software pageflip).
//
// Как работает режим DLP: каждое обновление экрана (~120 Гц) рисуется
// ОДИН глаз на весь экран, по очереди L, R, L, R... Проектор вставляет
// DLP-Link импульс между кадрами, очки открывают затворы по очереди.
// Чётность глаза считается по номеру обновления экрана (из меток времени
// requestAnimationFrame), а не по числу вызовов: если браузер пропустил
// обновление, следующий кадр всё равно попадёт в правильный глаз.

const $ = (id) => document.getElementById(id);
const canvas = $('gl'), video = $('video'), stage = $('stage'), clip = $('clip');
const params = new URLSearchParams(location.hash.slice(1));

const state = {
  out: params.get('mode') || load('out', 'dlp'),   // dlp | anaglyph | 2d | sbs
  layout: 'auto',                                  // auto | sbs | ou | 2d
  swap: false,
  test: false,
  movieKey: null,
  movieName: '',
  showDiag: false,
  // точная синхронизация пар: L и R всегда из одного кадра фильма (чуть тяжелее).
  // Лёгкий режим (клавиша P) — для слабой графики: браузер показывает видео сам.
  pairLock: load('pairLock', true),
};

// ---------- хранение настроек ----------
function load(k, def) { try { const v = localStorage.getItem('c3d.' + k); return v === null ? def : JSON.parse(v); } catch { return def; } }
function save(k, v) { try { localStorage.setItem('c3d.' + k, JSON.stringify(v)); } catch {} }
function movieSettings(key) { return load('movie.' + key, {}); }
function saveMovie(patch) {
  if (!state.movieKey) return;
  save('movie.' + state.movieKey, Object.assign(movieSettings(state.movieKey), patch));
}

// ---------- WebGL ----------
const gl = canvas.getContext('webgl', { antialias: false, alpha: false, preserveDrawingBuffer: false,
  powerPreference: 'high-performance' });
if (!gl) document.body.innerHTML = '<p style="padding:30px">WebGL недоступен. Обновите Chrome/Edge или видеодрайвер.</p>';

const VS = `attribute vec2 p; varying vec2 uv;
void main(){ uv = vec2(p.x*0.5+0.5, 0.5-p.y*0.5); gl_Position = vec4(p,0.,1.); }`;
const FS = `precision mediump float; varying vec2 uv; uniform sampler2D tex;
uniform vec4 rA; uniform vec4 rB; uniform int mode;
void main(){
  vec3 a = texture2D(tex, rA.xy + uv*rA.zw).rgb;
  if (mode == 0) { gl_FragColor = vec4(a,1.); return; }
  vec3 b = texture2D(tex, rB.xy + uv*rB.zw).rgb;
  // анаглиф красный-голубой, матрицы Dubois
  vec3 c = vec3(
    dot(a, vec3( 0.456, 0.500, 0.176)) + dot(b, vec3(-0.043,-0.088,-0.002)),
    dot(a, vec3(-0.040,-0.038,-0.016)) + dot(b, vec3( 0.378, 0.734,-0.018)),
    dot(a, vec3(-0.015,-0.021,-0.005)) + dot(b, vec3(-0.072,-0.113, 1.226)));
  gl_FragColor = vec4(clamp(c,0.,1.),1.);
}`;
function sh(type, src) { const s = gl.createShader(type); gl.shaderSource(s, src); gl.compileShader(s);
  if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(s)); return s; }
const prog = gl.createProgram();
gl.attachShader(prog, sh(gl.VERTEX_SHADER, VS)); gl.attachShader(prog, sh(gl.FRAGMENT_SHADER, FS));
gl.linkProgram(prog); gl.useProgram(prog);
const buf = gl.createBuffer(); gl.bindBuffer(gl.ARRAY_BUFFER, buf);
gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1,-1, 1,-1, -1,1, 1,1]), gl.STATIC_DRAW);
const aP = gl.getAttribLocation(prog, 'p'); gl.enableVertexAttribArray(aP); gl.vertexAttribPointer(aP, 2, gl.FLOAT, false, 0, 0);
const uA = gl.getUniformLocation(prog, 'rA'), uB = gl.getUniformLocation(prog, 'rB'), uMode = gl.getUniformLocation(prog, 'mode');

function mkTex() { const t = gl.createTexture(); gl.bindTexture(gl.TEXTURE_2D, t);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR); gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE); gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
  gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGB, 1, 1, 0, gl.RGB, gl.UNSIGNED_BYTE, new Uint8Array(3)); return t; }
const videoTex = mkTex(), testTex = mkTex();
let videoTexW = 0, videoTexH = 0;

// ---------- тестовая стереопара для проверки очков ----------
const testCanvas = document.createElement('canvas');
function drawTestPattern(t) {
  const W = 1280, H = 720; testCanvas.width = W * 2; testCanvas.height = H;
  const c = testCanvas.getContext('2d');
  const eyes = [{ x: 0, bg: '#5a0d0d', fg: '#ff6060', text: 'ЛЕВЫЙ ГЛАЗ', arrow: '◀', d: +1 },
                { x: W, bg: '#0d2a5a', fg: '#60a8ff', text: 'ПРАВЫЙ ГЛАЗ', arrow: '▶', d: -1 }];
  for (const e of eyes) {
    c.save(); c.translate(e.x, 0);
    c.fillStyle = e.bg; c.fillRect(0, 0, W, H);
    c.fillStyle = e.fg; c.textAlign = 'center'; c.textBaseline = 'middle';
    c.font = 'bold 110px Segoe UI, Arial'; c.fillText(e.arrow + ' ' + e.text, W / 2, 150);
    c.font = '34px Segoe UI, Arial'; c.fillStyle = '#ddd';
    c.fillText('Закройте один глаз: каждый глаз должен видеть ТОЛЬКО свою надпись', W / 2, 260);
    // шар «перед экраном»: отрицательный параллакс (у левого глаза правее, у правого левее)
    const bob = Math.sin(t / 700) * 40;
    c.fillStyle = '#ffd23a'; c.beginPath(); c.arc(W / 2 + e.d * 28, 470 + bob, 90, 0, 7); c.fill();
    c.fillStyle = '#222'; c.font = 'bold 30px Segoe UI'; c.fillText('ближе', W / 2 + e.d * 28, 470 + bob);
    // рамки «за экраном»
    c.strokeStyle = '#fff'; c.lineWidth = 8; c.strokeRect(120 - e.d * 14, 360, 260, 240);
    c.strokeRect(W - 380 - e.d * 14, 360, 260, 240);
    c.restore();
  }
  gl.bindTexture(gl.TEXTURE_2D, testTex);
  gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, testCanvas);
}

// ---------- формат стереопары ----------
function detectLayout(name, w, h) {
  const n = (name || '').toLowerCase();
  if (/(^|[^a-z])(h-?ou|half-?ou|ou|tab|h-?tab|top-?bottom|over-?under|abl|tb|3d-?ou)([^a-z]|$)/.test(n)) return 'ou';
  if (/(^|[^a-z])(h-?sbs|half-?sbs|f-?sbs|full-?sbs|sbs|side-?by-?side|lr|3d-?sbs)([^a-z]|$)/.test(n)) return 'sbs';
  if (w && h) { if (w / h >= 3) return 'sbs'; if (h / w >= 0.9) return 'ou'; }
  return 'sbs';   // самый частый вариант у скачиваемых 3D-фильмов (Half-SBS)
}
function effLayout() {
  if (state.test) return 'sbs';
  return state.layout === 'auto' ? detectLayout(state.movieName, video.videoWidth, video.videoHeight) : state.layout;
}
// прямоугольники левого/правого кадра в текстуре и пропорции кадра одного глаза
function eyeRects(layout, w, h) {
  if (layout === 'sbs') {
    const full = (w / 2) / h; // Full-SBS 3840x1080 -> 16:9 на глаз; Half-SBS -> растянуть до кадра
    return { L: [0, 0, .5, 1], R: [.5, 0, .5, 1], aspect: full >= 1.2 ? full : w / h };
  }
  if (layout === 'ou') {
    const full = w / (h / 2);
    return { L: [0, 0, 1, .5], R: [0, .5, 1, .5], aspect: full <= 2.5 && w / h < 1.2 ? full : w / h };
  }
  return { L: [0, 0, 1, 1], R: [0, 0, 1, 1], aspect: w / h };
}

// ---------- синхронизация с обновлением экрана ----------
const vs = { last: 0, period: 0, deltas: [], index: 0, missed: 0, missedRecent: [], frames: 0, uploads: 0 };
function estimatePeriod(dt) {
  vs.deltas.push(dt); if (vs.deltas.length > 240) vs.deltas.shift();
  // медиана; пересчёт раз в 30 кадров, чтобы не мусорить памятью на каждом кадре
  if (vs.period && vs.frames % 30 !== 0) return;
  const s = vs.deltas.slice().sort((a, b) => a - b);
  vs.period = s[Math.floor(s.length / 2)];
}

// ---------- DLP / 2D без WebGL ----------
// Видео (или тестовая картинка) лежит в окне #clip; каждый кадр меняется только сдвиг,
// чтобы в окне была видна нужная половина. Браузер рисует видео сам, без копирования кадра.
let path = '', cssSrc = null, cssSize = '';
// DLP: «снимок» кадра фильма делается только в начале пары L/R, оба глаза берут его из одного снимка
const snap = document.createElement('canvas'), snapCtx = snap.getContext('2d', { alpha: false });
const hold = document.createElement('div'); hold.style.display = 'none'; document.body.appendChild(hold);
function updateSnap() {
  const w = video.videoWidth, h = video.videoHeight;
  if (!w || video.readyState < 2) return;
  // снимок не больше, чем нужно проектору: каждый глаз — не крупнее окна на экране
  const r = eyeRects(effLayout(), w, h);
  let vw = viewport.w, vh = vw / r.aspect;
  if (vh > viewport.h) { vh = viewport.h; vw = vh * r.aspect; }
  const sw = Math.round(w * Math.min(1, vw / (w * r.L[2]))), sh = Math.round(h * Math.min(1, vh / (h * r.L[3])));
  if (snap.width !== sw || snap.height !== sh) { snap.width = sw; snap.height = sh; cssSize = ''; }
  snapCtx.drawImage(video, 0, 0, sw, sh); newVideoFrame = false; vs.uploads++;
}
const viewport = { w: innerWidth, h: innerHeight };
addEventListener('resize', () => { viewport.w = innerWidth; viewport.h = innerHeight; cssSize = ''; });
function usePath(p) {
  if (p === path) return;
  path = p;
  canvas.style.display = p === 'gl' ? 'block' : 'none';
  stage.style.display = p === 'css' ? 'block' : 'none';
}
function cssFrame(leftNow) {
  const src = state.test ? testCanvas : state.out === 'dlp' && state.pairLock ? snap : video;
  if (cssSrc !== src) { clip.replaceChildren(src); if (src !== video) hold.appendChild(video); cssSrc = src; cssSize = ''; }
  const w = src === video ? video.videoWidth : src.width, h = src === video ? video.videoHeight : src.height;
  if (!w || !h) return;
  const r = eyeRects(effLayout(), w, h);
  const L = state.swap ? r.R : r.L, R = state.swap ? r.L : r.R;
  let rect, aspect = r.aspect;
  if (state.out === 'sbs') { rect = [0, 0, 1, 1]; aspect = w / h; }
  else if (state.out === '2d') rect = L;
  else rect = leftNow ? L : R;   // dlp
  // окно с чёрными полями
  let vw = viewport.w, vh = vw / aspect;
  if (vh > viewport.h) { vh = viewport.h; vw = vh * aspect; }
  const ew = vw / rect[2], eh = vh / rect[3];
  const size = `${vw}x${vh}x${ew}x${eh}`;
  if (size !== cssSize) {
    cssSize = size;
    Object.assign(clip.style, { left: (viewport.w - vw) / 2 + 'px', top: (viewport.h - vh) / 2 + 'px', width: vw + 'px', height: vh + 'px' });
    src.style.width = ew + 'px'; src.style.height = eh + 'px';
  }
  src.style.transform = `translate(${-rect[0] * ew}px, ${-rect[1] * eh}px)`;
}

let newVideoFrame = false, videoFrames = 0;
function onVideoFrame() { newVideoFrame = true; videoFrames++; video.requestVideoFrameCallback(onVideoFrame); }
if ('requestVideoFrameCallback' in HTMLVideoElement.prototype) video.requestVideoFrameCallback(onVideoFrame);
else video.addEventListener('timeupdate', () => (newVideoFrame = true));

function resize() {
  const dpr = window.devicePixelRatio || 1;
  const w = Math.round(canvas.clientWidth * dpr), h = Math.round(canvas.clientHeight * dpr);
  if (canvas.width !== w || canvas.height !== h) { canvas.width = w; canvas.height = h; }
}

function frame(ts) {
  requestAnimationFrame(frame);
  // номер обновления экрана (с учётом пропущенных)
  if (vs.last) {
    const dt = ts - vs.last;
    if (dt > 2) {   // < 2 мс — это не обновление экрана (двойной вызов при старте)
      estimatePeriod(dt);
      if (vs.deltas.length < 30) vs.period = 0;   // пока частота не измерена, пропуски не считаем
      const steps = vs.period ? Math.max(1, Math.round(dt / vs.period)) : 1;
      // перерыв больше 0,5 с — окно было скрыто/перекрыто, это не пропуск кадров
      if (steps > 1 && dt < 500) { vs.missed += steps - 1; vs.missedRecent.push(performance.now()); }
      else if (dt >= 500) vs.hidden = (vs.hidden || 0) + 1;
      vs.index += steps;
    }
  }
  vs.last = ts; vs.frames++;

  const out = state.out;
  const leftNow = (vs.index & 1) === 0;          // чётное обновление = левый глаз (до перестановки)
  const firstOfPair = leftNow;

  // 2D и «как в файле» — браузер рисует видео сам (легче всего).
  // DLP — снимок кадра в начале пары, чтобы левый и правый глаз всегда показывали один кадр фильма.
  // Анаглиф — через WebGL (нужно смешивать оба глаза).
  if (out !== 'anaglyph') {
    if (out === 'dlp' && state.pairLock && !state.test && firstOfPair && (newVideoFrame || !snap.width)) updateSnap();
    usePath('css'); cssFrame(leftNow); return;
  }
  usePath('gl');
  resize();

  let texW, texH;
  if (state.test) {   // тестовая картинка рисуется один раз при включении теста
    gl.bindTexture(gl.TEXTURE_2D, testTex); texW = testCanvas.width; texH = testCanvas.height;
  } else {
    gl.bindTexture(gl.TEXTURE_2D, videoTex);
    // в DLP новый кадр видео берём только в начале пары, чтобы L и R были из одного кадра фильма
    if (video.readyState >= 2 && (newVideoFrame || !videoTexW) && (out !== 'dlp' || firstOfPair || !videoTexW)) {
      try {
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGB, gl.RGB, gl.UNSIGNED_BYTE, video);
        videoTexW = video.videoWidth; videoTexH = video.videoHeight; newVideoFrame = false; vs.uploads++;
      } catch (e) { showError('Не удалось взять кадр видео: ' + e.message); }
    }
    texW = videoTexW; texH = videoTexH;
  }

  gl.viewport(0, 0, canvas.width, canvas.height);
  gl.clearColor(0, 0, 0, 1); gl.clear(gl.COLOR_BUFFER_BIT);
  if (!texW) return;

  const r = eyeRects(effLayout(), texW, texH);
  const L = state.swap ? r.R : r.L, R = state.swap ? r.L : r.R;
  let mode = 0, rA, rB = R, aspect = r.aspect;
  if (out === 'sbs') { rA = [0, 0, 1, 1]; aspect = texW / texH; }
  else if (out === '2d') rA = L;
  else if (out === 'anaglyph') { mode = 1; rA = L; }
  else rA = leftNow ? L : R;   // dlp

  // вписать с чёрными полями
  const cw = canvas.width, ch = canvas.height;
  let vw = cw, vh = Math.round(cw / aspect);
  if (vh > ch) { vh = ch; vw = Math.round(ch * aspect); }
  gl.viewport((cw - vw) >> 1, (ch - vh) >> 1, vw, vh);
  gl.uniform4fv(uA, rA); gl.uniform4fv(uB, rB); gl.uniform1i(uMode, mode);
  gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
}
requestAnimationFrame(frame);

// ---------- диагностика ----------
const gpuName = (() => { try { const x = gl.getExtension('WEBGL_debug_renderer_info');
  return x ? gl.getParameter(x.UNMASKED_RENDERER_WEBGL) : gl.getParameter(gl.RENDERER); } catch { return '?'; } })();
function diagInfo() {
  const now = performance.now();
  vs.missedRecent = vs.missedRecent.filter((t) => now - t < 60000);
  const hz = vs.period ? 1000 / vs.period : 0;
  const q = video.getVideoPlaybackQuality ? video.getVideoPlaybackQuality() : {};
  return {
    hz: +hz.toFixed(2), missedTotal: vs.missed, hiddenGaps: vs.hidden || 0, missedLastMin: vs.missedRecent.length, rafFrames: vs.frames,
    out: state.out, layout: effLayout(), swap: state.swap, test: state.test, pairLock: state.pairLock,
    video: video.videoWidth ? `${video.videoWidth}x${video.videoHeight}` : '-',
    videoFramesShown: videoFrames, textureUploads: vs.uploads, droppedVideo: q.droppedVideoFrames || 0,
    screen: `${screen.width}x${screen.height} dpr=${devicePixelRatio}`, canvas: `${canvas.width}x${canvas.height}`,
    fullscreen: !!document.fullscreenElement, time: +video.currentTime.toFixed(1), paused: video.paused,
    error: video.error ? video.error.code : null, gpu: gpuName,
  };
}
window.__diag = diagInfo;
setInterval(() => {
  const d = diagInfo();
  if (state.showDiag) {
    const bad = state.out === 'dlp' && (d.hz < 110 || d.hz > 130);
    $('diag').innerHTML =
      `Обновление экрана: <span class="${bad ? 'bad' : ''}">${d.hz} Гц</span>${bad ? '  ← для DLP нужно ~120 Гц!' : ''}\n` +
      `Пропущено обновлений: ${d.missedLastMin} за минуту (всего ${d.missedTotal})\n` +
      `Режим: ${d.out}${d.test ? ' + тест' : ''}  формат: ${d.layout}  глаза: ${d.swap ? 'поменяны' : 'обычно'}  пары: ${d.pairLock ? 'точные' : 'лёгкие (P)'}\n` +
      `Видео: ${d.video}  потеряно кадров видео: ${d.droppedVideo}\n` +
      `Экран: ${d.screen}  холст: ${d.canvas}  полный экран: ${d.fullscreen ? 'да' : 'нет'}\n` +
      `Видеокарта: ${d.gpu}`;
  }
  // браузер синхронизируется с ОСНОВНЫМ дисплеем Windows; если это не проектор, DLP не заработает
  const wrongHz = state.out === 'dlp' && vs.frames > 240 && (d.hz < 110 || d.hz > 130);
  $('hzWarn').hidden = !wrongHz;
  if (wrongHz) $('hzWarnHz').textContent = Math.round(d.hz);
  updateBar();
  if (state.movieKey && !video.paused && ++saveTick % 10 === 0) saveMovie({ pos: video.currentTime });
}, 500);
let saveTick = 0;

// ---------- UI ----------
function toast(msg, ms = 1800) { const t = $('toast'); t.textContent = msg; t.hidden = false;
  clearTimeout(toast.h); toast.h = setTimeout(() => (t.hidden = true), ms); }
function showError(msg) { toast(msg, 6000); console.error(msg); }
const OUT_NAMES = { dlp: 'DLP 3D 120 Гц', anaglyph: 'Анаглиф', '2d': '2D', sbs: 'Как в файле' };
const LAYOUT_NAMES = { sbs: 'SBS', ou: 'OU', '2d': '2D' };

function refreshButtons() {
  document.querySelectorAll('#outSeg button').forEach((b) => b.classList.toggle('on', b.dataset.out === state.out));
  document.querySelectorAll('#layoutSeg button').forEach((b) => b.classList.toggle('on', b.dataset.layout === state.layout));
  $('swapBtn').classList.toggle('on', state.swap);
  $('testBtn').classList.toggle('on', state.test);
}
function setOut(o) { state.out = o; save('out', o); refreshButtons(); toast('Режим: ' + OUT_NAMES[o]); }
function setLayout(l) { state.layout = l; saveMovie({ layout: l }); refreshButtons(); renderLibrary();
  toast('Формат: ' + (l === 'auto' ? 'авто (' + LAYOUT_NAMES[effLayout()] + ')' : LAYOUT_NAMES[l])); }
function toggleSwap() { state.swap = !state.swap; if (state.test) save('testSwap', state.swap); else saveMovie({ swap: state.swap });
  refreshButtons(); toast(state.swap ? 'Глаза поменяны местами' : 'Глаза: обычный порядок'); }
function toggleTest(on = !state.test) {
  state.test = on;
  if (on) { video.pause(); drawTestPattern(0); state.swap = load('testSwap', false); showMenu(false); toast('Тест очков. S — поменять глаза, Esc — выход', 4000); }
  else state.swap = state.movieKey ? !!movieSettings(state.movieKey).swap : false;
  refreshButtons();
}
function showMenu(on) { $('menu').hidden = !on; if (on) { $('bar').hidden = true; document.body.classList.remove('hidecursor'); } }
function menuOpen() { return !$('menu').hidden; }

function fmtTime(s) { s = Math.max(0, Math.floor(s || 0)); const h = Math.floor(s / 3600), m = Math.floor(s / 60) % 60, x = s % 60;
  return (h ? h + ':' + String(m).padStart(2, '0') : m) + ':' + String(x).padStart(2, '0'); }
let seeking = false;
function updateBar() {
  $('time').textContent = fmtTime(video.currentTime) + ' / ' + fmtTime(video.duration);
  if (!seeking && video.duration) $('seek').value = Math.round(video.currentTime / video.duration * 1000);
  $('badge').textContent = `${OUT_NAMES[state.out]} · ${LAYOUT_NAMES[effLayout()]}${state.swap ? ' · L↔R' : ''}`;
}
$('seek').addEventListener('input', () => { seeking = true; });
$('seek').addEventListener('change', () => { if (video.duration) video.currentTime = $('seek').value / 1000 * video.duration; seeking = false; });

// показывать панель и курсор при движении мыши
let idleTimer;
function poke() {
  document.body.classList.remove('hidecursor');
  if (!menuOpen() && state.movieKey && !state.test) $('bar').hidden = false;
  clearTimeout(idleTimer);
  idleTimer = setTimeout(() => { if (!menuOpen()) { $('bar').hidden = true; document.body.classList.add('hidecursor'); } }, 2500);
}
document.addEventListener('mousemove', poke);

// ---------- открытие фильмов ----------
function openMovie(src, name, key) {
  toggleTest(false);
  state.movieKey = key; state.movieName = name;
  const st = movieSettings(key);
  state.layout = st.layout || 'auto'; state.swap = !!st.swap;
  videoTexW = 0;
  video.src = src;
  video.onloadedmetadata = () => {
    toast(`${name} — формат ${LAYOUT_NAMES[effLayout()]}${state.layout === 'auto' ? ' (авто)' : ''}`, 3000);
    if (st.pos && st.pos > 5 && st.pos < video.duration - 30) { video.currentTime = st.pos; toast('Продолжение с ' + fmtTime(st.pos), 3000); }
  };
  video.onerror = () => showError('Не удалось открыть «' + name + '». Перетащите файл на ADD-MOVIE.cmd — он перепакует его в совместимый MP4.');
  video.play().catch(() => {});
  refreshButtons(); showMenu(false); poke();
  document.title = name + ' — 3D Кинотеатр';
}

function renderLibrary() {
  const lib = window.LIBRARY || [];
  const box = $('library'); box.innerHTML = '';
  $('libEmpty').hidden = lib.length > 0;
  for (const m of lib) {
    const el = document.createElement('div'); el.className = 'movie';
    const key = (m.folder || '') + '/' + m.name;
    const st = movieSettings(key);
    const lay = st.layout && st.layout !== 'auto' ? st.layout : detectLayout(m.name, m.w, m.h);
    el.innerHTML = `<span class="name"></span><span class="tag">${LAYOUT_NAMES[lay]}</span>` +
      (m.warn ? '<span class="tag warn"></span>' : '') + '<span class="meta"></span>';
    el.querySelector('.name').textContent = m.name;
    if (m.warn) el.querySelector('.warn').textContent = m.warn;
    el.querySelector('.meta').textContent = (m.folder || '') + ' · ' + (m.size / 1073741824).toFixed(1) + ' ГБ' + (st.pos > 5 ? ' · ' + fmtTime(st.pos) : '');
    el.onclick = () => openMovie(m.url, m.name, key);
    box.appendChild(el);
  }
}

$('openBtn').onclick = () => $('fileInput').click();
$('fileInput').onchange = (e) => { const f = e.target.files[0]; if (f) openMovie(URL.createObjectURL(f), f.name, f.name); };
document.addEventListener('dragover', (e) => e.preventDefault());
document.addEventListener('drop', (e) => { e.preventDefault(); const f = e.dataTransfer.files[0];
  if (f) openMovie(URL.createObjectURL(f), f.name, f.name); });

document.querySelectorAll('#outSeg button').forEach((b) => (b.onclick = () => setOut(b.dataset.out)));
document.querySelectorAll('#layoutSeg button').forEach((b) => (b.onclick = () => setLayout(b.dataset.layout)));
$('swapBtn').onclick = toggleSwap;
$('testBtn').onclick = () => { toggleTest(true); goFullscreen(); };
$('fsBtn').onclick = goFullscreen;
$('menuBtn').onclick = () => showMenu(true);
$('playBtn').onclick = togglePlay;

function goFullscreen() { if (!document.fullscreenElement) document.documentElement.requestFullscreen().catch(() => {}); }
function togglePlay() { if (!state.movieKey) return; if (video.paused) video.play(); else video.pause(); }
function seekBy(s) { if (state.movieKey) { video.currentTime = Math.max(0, video.currentTime + s); toast((s > 0 ? '+' : '') + s + ' с → ' + fmtTime(video.currentTime), 1000); } }

document.addEventListener('keydown', (e) => {
  const k = e.key, c = e.code;
  if (k === 'Tab' || c === 'KeyM') { e.preventDefault(); showMenu(!menuOpen()); return; }
  if (k === 'Escape') { if (state.test) { toggleTest(false); showMenu(true); } else if (!menuOpen()) showMenu(true); return; }
  if (k === ' ') { e.preventDefault(); togglePlay(); return; }
  if (k === 'ArrowRight') return seekBy(10);
  if (k === 'ArrowLeft') return seekBy(-10);
  if (k === 'PageUp') return seekBy(60);
  if (k === 'PageDown') return seekBy(-60);
  if (k === 'ArrowUp') { e.preventDefault(); video.volume = Math.min(1, video.volume + .1); return toast('Громкость ' + Math.round(video.volume * 100) + '%', 800); }
  if (k === 'ArrowDown') { e.preventDefault(); video.volume = Math.max(0, video.volume - .1); return toast('Громкость ' + Math.round(video.volume * 100) + '%', 800); }
  if (c === 'KeyS') return toggleSwap();
  if (c === 'KeyP') { state.pairLock = !state.pairLock; save('pairLock', state.pairLock); vs.missed = 0; vs.missedRecent = [];
    return toast(state.pairLock ? 'Пары: точные (L и R из одного кадра)' : 'Пары: лёгкий режим (для слабой видеокарты)', 2500); }
  if (c === 'KeyT') { toggleTest(); if (state.test) goFullscreen(); return; }
  if (c === 'KeyI') { state.showDiag = !state.showDiag; $('diag').hidden = !state.showDiag; return; }
  if (c === 'KeyF') { if (document.fullscreenElement) document.exitFullscreen(); else goFullscreen(); return; }
  if (c === 'Digit1') return setOut('dlp');
  if (c === 'Digit2') return setOut('anaglyph');
  if (c === 'Digit3') return setOut('2d');
  if (c === 'Digit4') return setOut('sbs');
});

renderLibrary(); refreshButtons();
if (params.get('pairlock')) { state.pairLock = params.get('pairlock') === 'on'; save('pairLock', state.pairLock); }
if (params.get('test') === '1') toggleTest(true);
if (params.get('diag') === '1') { state.showDiag = true; $('diag').hidden = false; }
// log=1: раз в 5 с писать диагностику в консоль (попадает в chrome_debug.log профиля плеера)
if (params.get('log') === '1') setInterval(() => console.log('C3D_DIAG ' + JSON.stringify(diagInfo())), 5000);
const auto = params.get('open');
if (auto) { const m = (window.LIBRARY || []).find((x) => x.name === auto); if (m) openMovie(m.url, m.name, (m.folder || '') + '/' + m.name); }
