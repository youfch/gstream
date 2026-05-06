/**
 * Codec selector utilities for gStream webapp.
 *
 * On mobile (≤480px): replaces the <select> dropdown with a two-level
 * button group — top-level codec family buttons (Auto/H264/H265/AV1/VP9)
 * that expand to show variant sub-buttons when a family has multiple
 * profiles (e.g. H264 High L31, H264 Main L31, H264 Baseline L31).
 * A text line below shows the currently selected codec description.
 *
 * On desktop: the <select> is left untouched with full SDP text.
 *
 * The hidden <select> is always kept in sync so that the existing
 * setCodecPreferences() logic continues to work unchanged.
 */

/**
 * Detect if the current device is a mobile phone.
 *
 * Strategy (same as major video platforms like Tencent Video):
 * 1. Client Hints API (navigator.userAgentData) — Chromium browsers can
 *    directly report whether the device is mobile. Most reliable.
 * 2. UA string regex — fallback for non-Chromium browsers.
 *    MDN recommends checking for "Mobi" (covers Opera Mobile, etc.)
 *    combined with Android/iPhone/iPad patterns.
 *
 * We do NOT rely on viewport width or touch capability alone, because:
 * - Touchscreen laptops have touch but are not mobile
 * - Some mobile browsers report viewport > 480px (e.g. iQOO with VivoBrowser)
 */
function isMobileDevice() {
  // 1. Client Hints API (Chromium-based browsers)
  if (navigator.userAgentData) {
    return navigator.userAgentData.mobile;
  }

  // 2. UA string fallback
  return /Mobi|Android.*Mobile|iPhone|iPod|BlackBerry|IEMobile|Opera Mini/i.test(
    navigator.userAgent
  );
}

/**
 * Derive a human-readable short label from a full codec value string.
 * "video/H264 level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=64001f"
 *   → "H264 High L31"
 * "video/AV1 level-idx=5;profile=0;tier=0"
 *   → "AV1 Main L5"
 * "video/VP9 profile-id=0"
 *   → "VP9 P0"
 *
 * @param {string} value  Full codec value from RTCRtpSender.getCapabilities
 * @returns {string} Short human-readable label
 */
function codecLabel(value) {
  if (!value) return 'Auto';
  const mime = value.split(' ')[0];           // "video/H264"
  const family = mime.replace('video/', '');   // "H264"
  const fmtp = value.slice(mime.length + 1);   // "level-asymmetry-allowed=1;..."

  if (family === 'H264') {
    const plid = fmtp.match(/profile-level-id=([0-9a-f]+)/i);
    if (plid) {
      const id = parseInt(plid[1], 16);
      const profile = (id >> 8) & 0xFF;
      const level = (id & 0xFF) / 10;
      const profileName = profile === 100 ? 'High'
        : profile === 77 ? 'Main'
        : profile === 66 ? 'Baseline'
        : profile === 88 ? 'CBaseline'
        : `P${profile}`;
      return `H264 ${profileName} L${level}`;
    }
    return 'H264';
  }

  if (family === 'H265') {
    const lid = fmtp.match(/level-id=(\d+)/);
    const pid = fmtp.match(/profile-id=(\d+)/);
    const level = lid ? (parseInt(lid[1]) / 30) : '?';
    const profileName = pid && pid[1] === '1' ? 'Main' : pid ? 'High' : '';
    return `H265 ${profileName} L${level}`.trim();
  }

  if (family === 'AV1') {
    const lidx = fmtp.match(/level-idx=(\d+)/);
    const level = lidx ? parseInt(lidx[1]) : '?';
    return `AV1 Main L${level}`;
  }

  if (family === 'VP9') {
    const pid = fmtp.match(/profile-id=(\d+)/);
    return pid ? `VP9 P${pid[1]}` : 'VP9';
  }

  return family;
}

/**
 * Build the mobile codec button group inside `container`.
 *
 * Layout:
 *   [Auto] [H264] [H265] [AV1] [VP9]          ← family buttons
 *   [H264 High L31] [H264 Main L31] ...        ← variant sub-buttons (when family has multiple profiles)
 *   ▸ H264 High L31                             ← current selection text
 *
 * @param {HTMLSelectElement} selectEl  The hidden <select> to keep in sync
 * @param {HTMLElement} container       The .gs-codec-btn-group container
 * @param {function} [onSelect]         Optional callback after selection (for setCodecPreferences)
 */
export function setupMobileCodecUI(selectEl, container, onSelect) {
  if (!container || !selectEl) return;

  const mobile = isMobileDevice();

  // Always control DOM visibility from JS (more reliable than CSS media query alone)
  const desktopBox = selectEl.closest('.gs-codec-desktop');
  const mobileBox = container.closest('.gs-codec-mobile');
  if (desktopBox) desktopBox.style.display = mobile ? 'none' : '';
  if (mobileBox) mobileBox.style.display = mobile ? 'block' : 'none';

  if (!mobile) return;

  // Build variant map from getCapabilities (what the browser actually reports)
  const codecs = RTCRtpSender.getCapabilities('video').codecs;
  const reportedVariants = new Map(); // familyName → [{value, label}]
  for (const codec of codecs) {
    if (['video/red', 'video/ulpfec', 'video/rtx'].includes(codec.mimeType)) continue;
    const family = codec.mimeType.replace('video/', '');
    const value = (codec.mimeType + ' ' + (codec.sdpFmtpLine || '')).trim();
    if (!reportedVariants.has(family)) reportedVariants.set(family, []);
    reportedVariants.get(family).push({ value, label: codecLabel(value) });
  }

  // Hard-coded family list — always show all codec families.
  // Some browsers (e.g. Chrome Android on Dimensity 9200+) support AV1
  // via SDP negotiation in Auto mode but don't list it in getCapabilities.
  // By showing all families, users can explicitly request a codec and
  // let SDP negotiation determine if it's actually usable.
  const ALL_FAMILIES = ['Auto', 'H264', 'H265', 'AV1', 'VP9'];

  // Fallback profiles for families not reported by getCapabilities.
  // These match the SDP parameters defined in the project (see README).
  const FALLBACK_PROFILES = {
    H264: { value: 'video/H264 level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=64001f', label: 'H264 High L31' },
    H265: { value: 'video/H265 level-id=123;profile-id=1;tier-flag=0;tx-mode=SRST', label: 'H265 Main L41' },
    AV1:  { value: 'video/AV1 level-idx=5;profile=0;tier=0', label: 'AV1 Main L5' },
    VP9:  { value: 'video/VP9 profile-id=0', label: 'VP9 P0' },
  };

  // --- Family buttons row ---
  const familyRow = document.createElement('div');
  familyRow.className = 'gs-codec-family-row';

  // --- Variant sub-buttons row ---
  const variantRow = document.createElement('div');
  variantRow.className = 'gs-codec-variant-row';

  // --- Current selection text ---
  const selectionText = document.createElement('div');
  selectionText.className = 'gs-codec-selection';
  selectionText.textContent = 'Auto';

  for (const family of ALL_FAMILIES) {
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'gs-codec-family-btn';
    btn.textContent = family;
    btn.dataset.family = family;

    // Default-select Auto
    if (family === 'Auto') {
      btn.classList.add('gs-codec-family-btn--active');
    }

    btn.addEventListener('click', () => {
      // Highlight active family
      familyRow.querySelectorAll('.gs-codec-family-btn').forEach(b => b.classList.remove('gs-codec-family-btn--active'));
      btn.classList.add('gs-codec-family-btn--active');

      // Clear variants
      variantRow.innerHTML = '';

      if (family === 'Auto') {
        selectionText.textContent = 'Auto';
        selectEl.selectedIndex = 0;
        if (onSelect) onSelect();
        return;
      }

      const variants = reportedVariants.get(family);

      if (!variants || variants.length === 0) {
        // Family not reported by getCapabilities — use fallback profile
        const fallback = FALLBACK_PROFILES[family];
        selectionText.textContent = fallback.label;
        syncSelect(selectEl, fallback.value, onSelect);
      } else if (variants.length === 1) {
        // Single reported variant — select directly
        selectionText.textContent = variants[0].label;
        syncSelect(selectEl, variants[0].value, onSelect);
      } else {
        // Multiple reported variants — show sub-buttons
        for (const v of variants) {
          const vBtn = document.createElement('button');
          vBtn.type = 'button';
          vBtn.className = 'gs-codec-variant-btn';
          vBtn.textContent = v.label;
          vBtn.addEventListener('click', () => {
            variantRow.querySelectorAll('.gs-codec-variant-btn').forEach(b => b.classList.remove('gs-codec-variant-btn--active'));
            vBtn.classList.add('gs-codec-variant-btn--active');
            selectionText.textContent = v.label;
            syncSelect(selectEl, v.value, onSelect);
          });
          variantRow.appendChild(vBtn);
        }
      }
    });

    familyRow.appendChild(btn);
  }

  container.appendChild(familyRow);
  container.appendChild(variantRow);
  container.appendChild(selectionText);
}

/**
 * Sync the hidden <select> to the chosen codec value, then fire callback.
 */
function syncSelect(selectEl, value, onSelect) {
  if (value === '') {
    selectEl.selectedIndex = 0;
  } else {
    for (let i = 0; i < selectEl.options.length; i++) {
      if (selectEl.options[i].value === value) {
        selectEl.selectedIndex = i;
        break;
      }
    }
  }
  if (onSelect) onSelect();
}
