const USER_ID = '1312924157578711071';
const LANYARD_WS = 'wss://api.lanyard.rest/socket';
const BIRTH_DATE = new Date('2011-06-25T00:00:00Z');
const NEXT_BIRTHDAY = new Date('2026-06-25T00:00:00Z');

const els = {
  banner: document.getElementById('profile-banner'),
  avatar: document.getElementById('avatar'),
  avatarDecoration: document.getElementById('avatar-decoration'),
  statusIcon: document.getElementById('status-icon'),
  displayName: document.getElementById('display-name-click'),
  atUsername: document.getElementById('at-username'),
  customStatus: document.getElementById('custom-status'),
  devices: document.getElementById('devices'),
  guildBadge: document.getElementById('guild-badge'),
  guildBadgeIcon: document.getElementById('guild-badge-icon'),
  guildBadgeTag: document.getElementById('guild-badge-tag'),
  profileLoader: document.getElementById('profile-loader'),
  timeText: document.getElementById('time-text'),
  ageText: document.getElementById('age-text'),
  spotifyCard: document.getElementById('spotify-card'),
  spotifyLoader: document.getElementById('spotify-loader'),
  albumArt: document.getElementById('album-art'),
  songName: document.getElementById('song-name'),
  artistName: document.getElementById('artist-name'),
  albumName: document.getElementById('album-name'),
  progressFill: document.getElementById('progress-fill'),
  timeElapsed: document.getElementById('time-elapsed'),
  timeRemaining: document.getElementById('time-remaining'),
  openSpotify: document.getElementById('open-spotify-btn'),
  songsContainer: document.getElementById('recent-songs-container'),
  refreshBtn: document.getElementById('refresh-songs-btn'),
  agePopup: document.getElementById('age-popup'),
  ageMs: document.getElementById('age-ms'),
  ageNext: document.getElementById('age-next'),
  closeAgeBtn: document.getElementById('close-age-btn'),
  ageOverlay: document.getElementById('age-overlay')
};

let spotifyInterval = null;
let ws = null;
let heartbeatInterval = null;

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
      updateSpotify(d);
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

  if (data.discord_user.banner) {
    els.banner.src = `https://cdn.discordapp.com/banners/${USER_ID}/${data.discord_user.banner}?size=512`;
  } else {
    els.banner.style.background = 'linear-gradient(135deg, #001a2e 0%, #00213e 100%)';
  }

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
  const statusColors = {
    online: '#3ba55d',
    idle: '#faa81a',
    dnd: '#ed4245',
    offline: '#747f8d',
    invisible: '#747f8d'
  };
  const statusIcons = {
    online: 'https://cdn.discordapp.com/emojis/1041892916159610900.webp?size=96',
    idle: 'https://cdn.discordapp.com/emojis/1041892914413228073.webp?size=96',
    dnd: 'https://cdn.discordapp.com/emojis/1041892913306931271.webp?size=96',
    offline: 'https://cdn.discordapp.com/emojis/1041892912237408276.webp?size=96',
    invisible: 'https://cdn.discordapp.com/emojis/1041892912237408276.webp?size=96'
  };

  els.statusIcon.src = statusIcons[status] || statusIcons.offline;
  els.statusIcon.style.background = statusColors[status] || statusColors.offline;

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

function updateSpotify(data) {
  const spotify = data.spotify;

  if (!spotify) {
    els.spotifyCard.classList.add('inactive');
    els.songName.textContent = 'Not Listening';
    els.artistName.textContent = 'No song currently playing.';
    els.albumName.textContent = '';
    els.albumArt.src = 'https://via.placeholder.com/80/1a1a1a/666?text=%E2%99%AA';
    els.progressFill.style.width = '0%';
    els.timeElapsed.textContent = '0:00';
    els.timeRemaining.textContent = '-0:00';
    els.openSpotify.href = '#';
    els.spotifyLoader.classList.add('hidden');
    if (spotifyInterval) {
      clearInterval(spotifyInterval);
      spotifyInterval = null;
    }
    return;
  }

  els.spotifyCard.classList.remove('inactive');
  els.songName.textContent = spotify.song;
  els.artistName.textContent = spotify.artist;
  els.albumName.textContent = spotify.album;
  els.albumArt.src = spotify.album_art_url;
  els.openSpotify.href = `https://open.spotify.com/track/${spotify.track_id}`;
  els.spotifyLoader.classList.add('hidden');

  const start = spotify.timestamps?.start;
  const end = spotify.timestamps?.end;

  if (start && end) {
    const total = end - start;

    function tick() {
      const now = Date.now();
      const elapsed = now - start;
      const remaining = total - elapsed;
      const pct = Math.min((elapsed / total) * 100, 100);

      els.progressFill.style.width = `${pct}%`;
      els.timeElapsed.textContent = formatTime(elapsed);
      els.timeRemaining.textContent = `-${formatTime(Math.max(remaining, 0))}`;

      if (elapsed >= total && spotifyInterval) {
        clearInterval(spotifyInterval);
        spotifyInterval = null;
      }
    }

    tick();
    if (spotifyInterval) clearInterval(spotifyInterval);
    spotifyInterval = setInterval(tick, 1000);
  }
}

async function fetchRecentStreams() {
  els.songsContainer.innerHTML = '<div class="loading-overlay" style="position:static;background:none;padding:40px 0;"><span>Loading..</span></div>';

  try {
    const res = await fetch('https://api.stats.fm/api/v1/users/icegodftbl/recent?limit=10');
    if (!res.ok) throw new Error('fail');
    const data = await res.json();
    renderSongs(data.items || []);
  } catch {
    els.songsContainer.innerHTML = '<div style="text-align:center;padding:20px;color:var(--text-muted);font-size:13px;">No recent streams available</div>';
  }
}

function renderSongs(items) {
  if (!items.length) {
    els.songsContainer.innerHTML = '<div style="text-align:center;padding:20px;color:var(--text-muted);font-size:13px;">No recent streams</div>';
    return;
  }

  els.songsContainer.innerHTML = items.map(item => {
    const track = item.track || {};
    const album = track.albums?.[0];
    const image = album?.image || 'https://via.placeholder.com/48/1a1a1a/666?text=%E2%99%AA';
    const playedAt = item.playedAt || item.endTime;
    const timeAgo = playedAt ? timeSince(new Date(playedAt)) : '';

    return `<div class="song-item">
      <img src="${image}" alt="" loading="lazy">
      <div class="song-item-info">
        <div class="song-item-title">${escapeHtml(track.name || 'Unknown')}</div>
        <div class="song-item-artist">${escapeHtml(track.artists?.map(a => a.name).join(', ') || 'Unknown Artist')}</div>
      </div>
      <div class="song-item-time">${timeAgo}</div>
    </div>`;
  }).join('');
}

function timeSince(date) {
  const seconds = Math.floor((new Date() - date) / 1000);
  const intervals = [
    { label: 'y', seconds: 31536000 },
    { label: 'mo', seconds: 2592000 },
    { label: 'd', seconds: 86400 },
    { label: 'h', seconds: 3600 },
    { label: 'm', seconds: 60 }
  ];

  for (const interval of intervals) {
    const count = Math.floor(seconds / interval.seconds);
    if (count > 0) return `${count}${interval.label} ago`;
  }
  return 'just now';
}

function escapeHtml(text) {
  const div = document.createElement('div');
  div.textContent = text;
  return div.innerHTML;
}

function updateAge() {
  const now = new Date();
  const ageMs = now - BIRTH_DATE;
  const ageYears = Math.floor(ageMs / (365.25 * 24 * 60 * 60 * 1000));
  els.ageText.textContent = ageYears;
}
updateAge();

let ageCounterInterval = null;

function openAgePopup() {
  els.agePopup.hidden = false;
  document.body.style.overflow = 'hidden';

  function tick() {
    const now = new Date();
    const ageMs = now - BIRTH_DATE;
    els.ageMs.textContent = ageMs.toLocaleString('en-US');

    const untilBirthday = NEXT_BIRTHDAY - now;
    if (untilBirthday > 0) {
      const days = Math.floor(untilBirthday / (1000 * 60 * 60 * 24));
      const hours = Math.floor((untilBirthday % (1000 * 60 * 60 * 24)) / (1000 * 60 * 60));
      const mins = Math.floor((untilBirthday % (1000 * 60 * 60)) / (1000 * 60));
      els.ageNext.textContent = `${days}d ${hours}h ${mins}m until 15`;
    } else {
      els.ageNext.textContent = 'Happy 15th birthday!';
    }
  }

  tick();
  ageCounterInterval = setInterval(tick, 1);
}

function closeAgePopup() {
  els.agePopup.hidden = true;
  document.body.style.overflow = '';
  if (ageCounterInterval) {
    clearInterval(ageCounterInterval);
    ageCounterInterval = null;
  }
}

els.displayName.addEventListener('click', openAgePopup);
els.closeAgeBtn.addEventListener('click', closeAgePopup);
els.ageOverlay.addEventListener('click', closeAgePopup);

connectLanyard();
fetchRecentStreams();
els.refreshBtn.addEventListener('click', fetchRecentStreams);
