// VRC Texture Painter listing page.
// The build renders this file with Scriban too, but it needs no template data:
// everything comes from index.html and from the live index.json, so the page
// stays correct even when the listing is rebuilt without a new release.
(() => {
  const body = document.body;
  const listingUrl = body.dataset.listingUrl;
  const packageId = body.dataset.packageId;
  const toast = document.getElementById('toast');
  let toastTimer;

  const showToast = (message, duration = 4000) => {
    toast.textContent = message;
    toast.hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => { toast.hidden = true; }, duration);
  };

  // Add to VCC: vcc:// deep link, with a hint when nothing handles it
  const vccLink = `vcc://vpm/addRepo?url=${encodeURIComponent(listingUrl)}`;
  document.querySelectorAll('[data-vcc-add]').forEach(link => {
    link.href = vccLink;
    link.addEventListener('click', () => {
      setTimeout(() => {
        if (document.hasFocus()) {
          showToast('Nothing opened? Install the VRChat Creator Companion, or add the listing URL manually.', 6000);
        }
      }, 2000);
    });
  });

  // Copy buttons
  const copyText = async text => {
    try {
      await navigator.clipboard.writeText(text);
      return true;
    } catch {
      const area = document.createElement('textarea');
      area.value = text;
      area.setAttribute('readonly', '');
      area.style.position = 'fixed';
      area.style.opacity = '0';
      document.body.appendChild(area);
      area.select();
      const ok = document.execCommand('copy');
      area.remove();
      return ok;
    }
  };

  document.querySelectorAll('[data-copy]').forEach(button => {
    button.addEventListener('click', async () => {
      const field = document.getElementById(button.dataset.copy);
      field.select();
      const ok = await copyText(field.value);
      const label = button.textContent;
      button.textContent = ok ? 'Copied' : 'Press Ctrl+C';
      setTimeout(() => { button.textContent = label; }, 1500);
    });
  });

  // Versions from the live listing
  const parseVersion = version => {
    const [core, pre = ''] = String(version).split('-');
    const nums = core.split('.').map(n => parseInt(n, 10) || 0);
    return { nums, pre };
  };

  const compareVersions = (a, b) => {
    const va = parseVersion(a), vb = parseVersion(b);
    for (let i = 0; i < 3; i++) {
      const diff = (va.nums[i] || 0) - (vb.nums[i] || 0);
      if (diff !== 0) return diff;
    }
    if (va.pre === vb.pre) return 0;
    if (!va.pre) return 1;   // a release beats its pre-releases
    if (!vb.pre) return -1;
    return va.pre.localeCompare(vb.pre, undefined, { numeric: true });
  };

  const renderVersions = versions => {
    const sorted = versions.slice().sort((a, b) => compareVersions(b.version, a.version));
    const latest = sorted.find(v => !String(v.version).includes('-')) || sorted[0];
    if (!latest) return;

    document.querySelectorAll('[data-latest-version]').forEach(el => {
      el.textContent = `v${latest.version}`;
    });
    document.querySelectorAll('[data-latest-zip]').forEach(el => {
      if (latest.url) el.href = latest.url;
    });

    const list = document.getElementById('versionList');
    list.replaceChildren();
    sorted.forEach(entry => {
      const item = document.createElement('li');
      const name = document.createElement('span');
      name.className = 'version-name';
      name.textContent = `v${entry.version}`;
      item.appendChild(name);

      if (String(entry.version).includes('-')) {
        const tag = document.createElement('span');
        tag.className = 'badge badge-soft';
        tag.textContent = 'Pre-release';
        item.appendChild(tag);
      } else if (entry === latest) {
        const tag = document.createElement('span');
        tag.className = 'badge';
        tag.textContent = 'Latest';
        item.appendChild(tag);
      }

      if (entry.url) {
        const link = document.createElement('a');
        link.href = entry.url;
        link.textContent = 'Download .zip';
        item.appendChild(link);
      }
      list.appendChild(item);
    });
    document.getElementById('versions').hidden = sorted.length === 0;
  };

  fetch('index.json', { cache: 'no-store' })
    .then(response => (response.ok ? response.json() : null))
    .then(listing => {
      const versions = listing && listing.packages && listing.packages[packageId] && listing.packages[packageId].versions;
      if (versions) renderVersions(Object.values(versions));
    })
    .catch(() => { /* opened without a server: keep the rendered values */ });
})();
