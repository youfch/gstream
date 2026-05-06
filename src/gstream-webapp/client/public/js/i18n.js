/**
 * gStream lightweight i18n module.
 *
 * - Supports English (en) and Chinese (zh).
 * - Persists language choice in localStorage.
 * - Auto-detects browser language on first visit.
 * - Provides t(key) helper and applyLocale() for data-i18n attributes.
 */

const STORAGE_KEY = 'gstream-lang';

const translations = {
  en: {
    // --- Landing page ---
    'hero.subtitle': 'Real-time WebRTC video streaming — low latency, high quality',
    'config.title': 'Server Configuration',
    'config.protocol': 'Signaling Protocol',
    'config.mode': 'Signaling Mode',
    'ice.title': 'ICE Servers',
    'ice.url': 'STUN or TURN URI:',
    'ice.username': 'TURN username:',
    'ice.password': 'TURN password:',
    'ice.add': 'Add Server',
    'ice.remove': 'Remove Server',
    'ice.reset': 'Reset to defaults',
    'samples.title': 'Streaming Samples',
    'samples.subtitle': 'Select a sample page below to start streaming.',
    'card.receiver.title': 'Receiver',
    'card.receiver.desc': 'Receive real-time video and audio from the game engine with codec selection and mouse lock support.',
    'card.bidirectional.title': 'Bidirectional',
    'card.bidirectional.desc': 'Send local camera video and receive remote video simultaneously. Requires Private mode.',
    'card.multiplay.title': 'Multiplay',
    'card.multiplay.desc': 'Multiplayer streaming with input data channel support. Connect as a guest to a shared session.',
    'card.videoplayer.title': 'VideoPlayer',
    'card.videoplayer.desc': 'Receive a camera stream with interactive controls. Requires Public mode.',
    'footer': 'gStream — Real-time WebRTC streaming for game engines',

    // --- Shared across sub-pages ---
    'back': '← Back',
    'codec.preferences': 'Codec preferences:',
    'codec.default': 'Default',
    'lock.cursor': 'Lock Cursor to Player:',
    'disconnect.peer': 'Disconnect peer on {id}.',

    // --- Warning messages ---
    'warning': 'Warning',
    'warning.private': 'This sample is not working on Private Mode.',
    'warning.public': 'This sample is not working on Public Mode.',
    'browser.noCodecSupport': 'Current Browser does not support <a href="https://developer.mozilla.org/en-US/docs/Web/API/RTCRtpTransceiver/setCodecPreferences">RTCRtpTransceiver.setCodecPreferences</a>.',

    // --- Receiver / Multiplay ---
    'start.streaming': 'Start Streaming',

    // --- Bidirectional ---
    'video.source': 'Video source: ',
    'audio.source': 'Audio source: ',
    'video.resolution': 'Video resolution: ',
    'camera.width': 'Camera width:',
    'camera.height': 'Camera height:',
    'start.video': 'Start Video',
    'set.up': 'Set Up',
    'hang.up': 'Hang Up',
    'local': 'Local',
    'remote': 'Remote',
    'connection.id': 'Connection ID:',
    'sending.resolution': 'Sending resolution:',
    'receiving.resolution': 'Receiving resolution:',
    'custom': 'Custom',

    // --- VideoPlayer ---
    'light.on': 'Light on',
    'light.off': 'Light off',
    'play.audio': 'Play audio',

    // --- Stats ---
    'stats.receiving': 'receiving stream stats',
    'stats.sending': 'sending stream stats',
    'stats.codec': 'Codec:',
    'stats.decoder': 'Decoder:',
    'stats.encoder': 'Encoder:',
    'stats.resolution': 'Resolution:',
    'stats.framerate': 'Framerate:',
    'stats.bitrate': 'Bitrate:',

    // --- ICE settings ---
    'ice.uri.invalid': 'URI scheme {scheme} is not valid',

    // --- Language ---
    'lang.en': 'EN',
    'lang.zh': '中文',
  },

  zh: {
    // --- Landing page ---
    'hero.subtitle': '实时 WebRTC 视频推流 — 低延迟，高画质',
    'config.title': '服务器配置',
    'config.protocol': '信令协议',
    'config.mode': '信令模式',
    'ice.title': 'ICE 服务器',
    'ice.url': 'STUN 或 TURN 地址：',
    'ice.username': 'TURN 用户名：',
    'ice.password': 'TURN 密码：',
    'ice.add': '添加服务器',
    'ice.remove': '移除服务器',
    'ice.reset': '恢复默认',
    'samples.title': '推流示例',
    'samples.subtitle': '选择下方示例页面开始推流。',
    'card.receiver.title': '接收端',
    'card.receiver.desc': '从游戏引擎接收实时视频和音频，支持编解码器选择和鼠标锁定。',
    'card.bidirectional.title': '双向推流',
    'card.bidirectional.desc': '同时发送本地摄像头视频和接收远端视频。需要 Private 模式。',
    'card.multiplay.title': '多人推流',
    'card.multiplay.desc': '支持输入数据通道的多人推流。以访客身份加入共享会话。',
    'card.videoplayer.title': '视频播放器',
    'card.videoplayer.desc': '接收摄像头流并提供交互控制。需要 Public 模式。',
    'footer': 'gStream — 面向游戏引擎的实时 WebRTC 推流',

    // --- Shared across sub-pages ---
    'back': '← 返回',
    'codec.preferences': '编解码器偏好：',
    'codec.default': '默认',
    'lock.cursor': '锁定鼠标到播放器：',
    'disconnect.peer': '已断开与 {id} 的连接。',

    // --- Warning messages ---
    'warning': '警告',
    'warning.private': '此示例在 Private 模式下无法工作。',
    'warning.public': '此示例在 Public 模式下无法工作。',
    'browser.noCodecSupport': '当前浏览器不支持 <a href="https://developer.mozilla.org/en-US/docs/Web/API/RTCRtpTransceiver/setCodecPreferences">RTCRtpTransceiver.setCodecPreferences</a>。',

    // --- Receiver / Multiplay ---
    'start.streaming': '开始推流',

    // --- Bidirectional ---
    'video.source': '视频源：',
    'audio.source': '音频源：',
    'video.resolution': '视频分辨率：',
    'camera.width': '摄像头宽度：',
    'camera.height': '摄像头高度：',
    'start.video': '启动视频',
    'set.up': '建立连接',
    'hang.up': '挂断',
    'local': '本地',
    'remote': '远端',
    'connection.id': '连接 ID：',
    'sending.resolution': '发送分辨率：',
    'receiving.resolution': '接收分辨率：',
    'custom': '自定义',

    // --- VideoPlayer ---
    'light.on': '开灯',
    'light.off': '关灯',
    'play.audio': '播放音频',

    // --- Stats ---
    'stats.receiving': '接收流统计',
    'stats.sending': '发送流统计',
    'stats.codec': '编解码器：',
    'stats.decoder': '解码器：',
    'stats.encoder': '编码器：',
    'stats.resolution': '分辨率：',
    'stats.framerate': '帧率：',
    'stats.bitrate': '码率：',

    // --- ICE settings ---
    'ice.uri.invalid': 'URI 协议 {scheme} 无效',

    // --- Language ---
    'lang.en': 'EN',
    'lang.zh': '中文',
  }
};

/**
 * Detect the best initial language.
 * Priority: localStorage > browser language > default (en)
 */
function detectLanguage() {
  const stored = window.localStorage.getItem(STORAGE_KEY);
  if (stored && translations[stored]) return stored;

  const browserLang = (navigator.language || navigator.userLanguage || 'en').toLowerCase();
  if (browserLang.startsWith('zh')) return 'zh';
  return 'en';
}

let _currentLang = detectLanguage();

/**
 * Get current language code.
 * @returns {'en'|'zh'}
 */
export function getLang() {
  return _currentLang;
}

/**
 * Set the current language and persist to localStorage.
 * @param {'en'|'zh'} lang
 */
export function setLang(lang) {
  if (!translations[lang]) return;
  _currentLang = lang;
  window.localStorage.setItem(STORAGE_KEY, lang);
}

/**
 * Translate a key. Supports {placeholder} interpolation.
 * @param {string} key
 * @param {Object} [params] - e.g. { id: '123' } replaces {id} in the string
 * @returns {string}
 */
export function t(key, params) {
  let text = translations[_currentLang]?.[key] ?? translations.en[key] ?? key;
  if (params) {
    for (const [k, v] of Object.entries(params)) {
      text = text.replace(new RegExp(`\\{${k}\\}`, 'g'), v);
    }
  }
  return text;
}

/**
 * Apply translations to all elements with data-i18n="key" attributes.
 * Also handles data-i18n-placeholder for placeholder text.
 */
export function applyLocale() {
  document.querySelectorAll('[data-i18n]').forEach(el => {
    const key = el.getAttribute('data-i18n');
    el.textContent = t(key);
  });
  document.querySelectorAll('[data-i18n-html]').forEach(el => {
    const key = el.getAttribute('data-i18n-html');
    el.innerHTML = t(key);
  });
  document.documentElement.lang = _currentLang;
}

/**
 * Create a language switcher element and append it to the given container.
 * @param {HTMLElement} container
 * @param {Function} [onChange] - callback after language change (for page-specific updates)
 */
export function createLangSwitcher(container, onChange) {
  const switcher = document.createElement('div');
  switcher.className = 'gs-lang-switcher';

  const languages = [
    { code: 'en', label: 'EN' },
    { code: 'zh', label: '中文' },
  ];

  languages.forEach(({ code, label }) => {
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'gs-lang-btn' + (_currentLang === code ? ' gs-lang-btn--active' : '');
    btn.textContent = label;
    btn.addEventListener('click', () => {
      if (_currentLang === code) return;
      setLang(code);
      // Update active state
      switcher.querySelectorAll('.gs-lang-btn').forEach(b => b.classList.remove('gs-lang-btn--active'));
      btn.classList.add('gs-lang-btn--active');
      applyLocale();
      if (onChange) onChange(code);
    });
    switcher.appendChild(btn);
  });

  container.appendChild(switcher);
  return switcher;
}
