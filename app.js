const USER_ID = '1312924157578711071';
const LANYARD_WS = 'wss://api.lanyard.rest/socket';
const BIRTH_DATE = new Date('2011-06-25T00:00:00Z');

const els = {
  banner: document.getElementById('profile-banner'),
  avatar: document.getElementById('avatar'),
  avatarDecoration: document.getElementById('avatar-decoration'),
  statusIcon: document.getElementById('status-icon'),
  displayName: document.querySelector('.display-name-inner'),
  atUsername: document.getElementById('at-username'),
  customStatus: document.getElementById('custom-status'),
  devices: document.getElementById('devices'),
  guildBadge: document.getElementById('guild-badge'),
  guildBadgeIcon: document.getElementById('guild-badge-icon'),
  guildBadgeTag: document.getElementById('guild-badge-tag'),
  profileLoader: document.getElementById('profile-loader'),
  timeText: document.getElementById('time-text'),
  ageShort: document.getElementById('age-short'),
  ageLong: document.getElementById('age-long'),
  ageTooltip: document.getElementById('age-tooltip'),
  activityCard: document.getElementById('spotify-card'),
  activityLoader: document.getElementById('spotify-loader'),
  activityTitle: document.getElementById('activity-title'),
  activityIconWrapper: document.getElementById('activity-icon-wrapper'),
  activityName: document.getElementById('activity-name'),
  activityDetail: document.getElementById('activity-detail'),
  activityState: document.getElementById('activity-state'),
  activityProgressContainer: document.getElementById('activity-progress-container'),
  activityProgressFill: document.getElementById('activity-progress-fill'),
  activityTimeElapsed: document.getElementById('activity-time-elapsed'),
  activityTimeRemaining: document.getElementById('activity-time-remaining'),
  stampGrid: document.getElementById('stamp-grid'),
  navBar: document.getElementById('nav-bar'),
};

let activityInterval = null;
let ws = null;
let heartbeatInterval = null;
let ageTooltipInterval = null;
let ageLongInterval = null;

function playSound(src) {
  const audio = new Audio(src);
  audio.volume = 0.3;
  audio.play().catch(() => {});
}

function updateTime() {
  const now = new Date();
  els.timeText.textContent = now.toLocaleTimeString('en-US', {
    hour: '2-digit',
    minute: '2-digit',
    hour12: true
  });
}
setInterval(updateTime, 1000);
updateTime();

function formatTime(ms) {
  const totalSeconds = Math.floor(ms / 1000);
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return `${minutes}:${seconds.toString().padStart(2, '0')}`;
}

function connectLanyard() {
  ws = new WebSocket(LANYARD_WS);

  ws.onopen = () => {
    ws.send(JSON.stringify({
      op: 2,
      d: { subscribe_to_id: USER_ID }
    }));
  };

  ws.onmessage = (event) => {
    const { t, d } = JSON.parse(event.data);

    if (t === 'INIT_STATE' || t === 'PRESENCE_UPDATE') {
      updateProfile(d);
      updateActivity(d);
    }

    if (d && d.heartbeat_interval) {
      if (heartbeatInterval) clearInterval(heartbeatInterval);
      heartbeatInterval = setInterval(() => {
        ws.send(JSON.stringify({ op: 3 }));
      }, d.heartbeat_interval);
    }
  };

  ws.onclose = () => {
    if (heartbeatInterval) clearInterval(heartbeatInterval);
    setTimeout(connectLanyard, 3000);
  };

  ws.onerror = () => {
    ws.close();
  };
}

function updateProfile(data) {
  if (!data.discord_user) return;

  const avatarHash = data.discord_user.avatar;
  els.avatar.src = avatarHash
    ? `https://cdn.discordapp.com/avatars/${USER_ID}/${avatarHash}?size=256`
    : `https://cdn.discordapp.com/embed/avatars/${parseInt(data.discord_user.discriminator || '0') % 5}.png`;

  if (data.discord_user.avatar_decoration_data?.asset) {
    els.avatarDecoration.src = `https://cdn.discordapp.com/avatar-decoration-presets/${data.discord_user.avatar_decoration_data.asset}.png?size=160`;
    els.avatarDecoration.style.display = 'block';
  } else {
    els.avatarDecoration.style.display = 'none';
  }

  const status = data.discord_status || 'offline';
  const statusFiles = {
    online: 'online.png',
    idle: 'moon.png',
    dnd: 'dnd.png',
    offline: 'offline.png',
    invisible: 'offline.png'
  };

  els.statusIcon.src = statusFiles[status] || 'offline.png';

  els.displayName.textContent = data.discord_user.global_name || data.discord_user.username;
  els.atUsername.textContent = `@${data.discord_user.username}`;

  const activity = data.activities?.find(a => a.type === 4);
  if (activity) {
    let html = '';
    if (activity.emoji?.id) {
      html += `<img src="https://cdn.discordapp.com/emojis/${activity.emoji.id}.png?size=16" class="custom-status-emoji" alt=""> `;
    } else if (activity.emoji?.name) {
      html += `${activity.emoji.name} `;
    }
    html += activity.state || '';
    els.customStatus.innerHTML = html;
  } else {
    els.customStatus.textContent = '';
  }

  const activeDevices = [];
  if (data.active_on_discord_desktop) activeDevices.push('desktop');
  if (data.active_on_discord_mobile) activeDevices.push('mobile');
  if (data.active_on_discord_web) activeDevices.push('web');

  const deviceIcons = {
    desktop: '<svg class="device-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="2" y="3" width="20" height="14" rx="2"/><path d="M8 21h8"/><path d="M12 17v4"/></svg>',
    mobile: '<svg class="device-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="5" y="2" width="14" height="20" rx="2"/><path d="M12 18h.01"/></svg>',
    web: '<svg class="device-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="10"/><path d="M2 12h20"/><path d="M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z"/></svg>'
  };

  els.devices.innerHTML = activeDevices.map(d => deviceIcons[d] || '').join('');

  if (data.discord_user.premium_type) {
    els.guildBadge.style.display = 'inline-flex';
    els.guildBadgeTag.textContent = 'NITRO';
  }

  els.profileLoader.classList.add('hidden');
}

function updateActivity(data) {
  if (activityInterval) {
    clearInterval(activityInterval);
    activityInterval = null;
  }

  const spotify = data.spotify;
  const gameActivity = data.activities?.find(a => a.type === 0 && a.application_id);

  if (spotify) {
    renderSpotify(spotify);
    return;
  }

  if (gameActivity) {
    renderGame(gameActivity);
    return;
  }

  renderNoActivity();
}

function renderSpotify(spotify) {
  els.activityTitle.textContent = 'Listening to Spotify';
  els.activityName.textContent = spotify.song;
  els.activityDetail.textContent = spotify.artist;
  els.activityState.textContent = spotify.album;
  els.activityIconWrapper.innerHTML = `<img src="${spotify.album_art_url}" alt="">`;
  els.activityProgressContainer.style.display = 'flex';

  const start = spotify.timestamps?.start;
  const end = spotify.timestamps?.end;

  if (start && end) {
    const total = end - start;

    function tick() {
      const now = Date.now();
      const elapsed = now - start;
      const remaining = total - elapsed;
      const pct = Math.min((elapsed / total) * 100, 100);

      els.activityProgressFill.style.width = `${pct}%`;
      els.activityTimeElapsed.textContent = formatTime(elapsed);
      els.activityTimeRemaining.textContent = `-${formatTime(Math.max(remaining, 0))}`;

      if (elapsed >= total && activityInterval) {
        clearInterval(activityInterval);
        activityInterval = null;
      }
    }

    tick();
    activityInterval = setInterval(tick, 1000);
  }

  els.activityLoader.classList.add('hidden');
}

function renderGame(activity) {
  els.activityTitle.textContent = 'Playing a game';
  els.activityName.textContent = activity.name;
  els.activityDetail.textContent = activity.details || '';
  els.activityState.textContent = activity.state || '';

  if (activity.assets?.large_image) {
    const imageUrl = activity.assets.large_image.startsWith('mp:')
      ? `https://media.discordapp.net/${activity.assets.large_image.replace('mp:', '')}`
      : `https://cdn.discordapp.com/app-assets/${activity.application_id}/${activity.assets.large_image}.png`;
    els.activityIconWrapper.innerHTML = `<img src="${imageUrl}" alt="">`;
  } else {
    els.activityIconWrapper.innerHTML = `<svg class="activity-default-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="6" width="20" height="12" rx="2"/><path d="M6 10h.01"/><path d="M10 10h.01"/><path d="M14 10h.01"/><path d="M18 10h.01"/></svg>`;
  }

  const start = activity.timestamps?.start;
  const end = activity.timestamps?.end;

  if (start || end) {
    els.activityProgressContainer.style.display = 'flex';

    function tick() {
      const now = Date.now();
      const elapsed = start ? now - start : 0;
      const remaining = end ? end - now : 0;
      const total = start && end ? end - start : 0;
      const pct = total > 0 ? Math.min((elapsed / total) * 100, 100) : 0;

      els.activityProgressFill.style.width = `${pct}%`;
      els.activityTimeElapsed.textContent = formatTime(elapsed);
      els.activityTimeRemaining.textContent = end ? `-${formatTime(Math.max(remaining, 0))}` : '';
    }

    tick();
    activityInterval = setInterval(tick, 1000);
  } else {
    els.activityProgressContainer.style.display = 'none';
  }

  els.activityLoader.classList.add('hidden');
}

function renderNoActivity() {
  els.activityTitle.textContent = 'No Activity';
  els.activityName.textContent = 'Not doing anything right now';
  els.activityDetail.textContent = '';
  els.activityState.textContent = '';
  els.activityIconWrapper.innerHTML = `<svg class="activity-default-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><path d="M16 16s-1.5-2-3.5-2S9 16 9 16"/><path d="M9 9h.01"/><path d="M15 9h.01"/></svg>`;
  els.activityProgressContainer.style.display = 'none';
  els.activityLoader.classList.add('hidden');
}

function updateAge() {
  const now = new Date();
  const ageMs = now - BIRTH_DATE;
  const ageYears = ageMs / (365.25 * 24 * 60 * 60 * 1000);
  els.ageShort.textContent = Math.floor(ageYears);
}
updateAge();

const ageWrapper = document.querySelector('.age-text-wrapper');

ageWrapper.addEventListener('mouseenter', () => {
  function tick() {
    const now = new Date();
    const ageMs = now - BIRTH_DATE;
    const ageYears = ageMs / (365.25 * 24 * 60 * 60 * 1000);
    els.ageLong.textContent = ageYears.toFixed(9) + ' years old';
  }
  tick();
  ageLongInterval = setInterval(tick, 50);
});

ageWrapper.addEventListener('mouseleave', () => {
  if (ageLongInterval) {
    clearInterval(ageLongInterval);
    ageLongInterval = null;
  }
});

const nameHover = document.getElementById('name-hover');

nameHover.addEventListener('mouseenter', () => {
  function tick() {
    const now = new Date();
    const ageMs = now - BIRTH_DATE;
    const ageYears = ageMs / (365.25 * 24 * 60 * 60 * 1000);
    els.ageTooltip.textContent = ageYears.toFixed(9) + ' years old';
  }
  tick();
  ageTooltipInterval = setInterval(tick, 50);
});

nameHover.addEventListener('mouseleave', () => {
  if (ageTooltipInterval) {
    clearInterval(ageTooltipInterval);
    ageTooltipInterval = null;
  }
});

function checkBadges() {
  const stamps = els.stampGrid.querySelectorAll('img');
  stamps.forEach(img => {
    img.onerror = () => {
      img.style.display = 'none';
    };
  });
}

const navBtns = document.querySelectorAll('.nav-btn');

function scrollToSection(targetId) {
  const target = document.getElementById(targetId);
  if (!target) return;
  target.scrollIntoView({ behavior: 'smooth', block: 'start' });
}

navBtns.forEach(btn => {
  btn.addEventListener('click', () => {
    const target = btn.dataset.target;
    const sound = btn.dataset.sound;

    navBtns.forEach(b => b.classList.remove('active'));
    btn.classList.add('active');

    if (sound) playSound(sound);
    scrollToSection(target);
  });
});

document.addEventListener('click', function(e) {
  var container = document.createElement('div');
  container.className = 'click-ripple';
  container.style.left = e.clientX + 'px';
  container.style.top = e.clientY + 'px';

  var flash = document.createElement('div');
  flash.className = 'click-flash';
  container.appendChild(flash);

  var ring = document.createElement('div');
  ring.className = 'click-ripple-ring';
  container.appendChild(ring);

  var count = 8;
  for (var i = 0; i < count; i++) {
    var p = document.createElement('div');
    p.className = 'click-particle';
    var size = (2 + Math.random() * 3).toFixed(1);
    p.style.width = size + 'px';
    p.style.height = size + 'px';

    var angle = (i / count) * 2 * Math.PI + (Math.random() - 0.5) * 0.5;
    var dist = 16 + Math.random() * 20;
    var dx = Math.cos(angle) * dist;
    var dy = Math.sin(angle) * dist;

    p.animate(
      [
        { transform: 'translate(0,0) scale(1)', opacity: 1 },
        {
          transform: 'translate(' + dx.toFixed(1) + 'px,' + dy.toFixed(1) + 'px) scale(0)',
          opacity: 0,
        },
      ],
      {
        duration: 400 + Math.random() * 300,
        easing: 'cubic-bezier(0.22, 1, 0.36, 1)',
        fill: 'forwards',
      },
    );

    container.appendChild(p);
  }

  document.body.appendChild(container);

  setTimeout(function() {
    if (container.parentNode) container.remove();
  }, 900);
});

checkBadges();
connectLanyard();
