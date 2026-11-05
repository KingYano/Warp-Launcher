'use strict';
/**
 * Warp — interface.
 *
 * Dans l'app, elle parle à l'hôte natif (Warp.exe) par messages : lancer un jeu, choisir un fichier,
 * enregistrer la collection. Ouverte dans un simple navigateur, elle passe en « aperçu » avec des
 * données d'exemple et des lancements simulés.
 */
(function () {
  const $ = (id) => document.getElementById(id);
  const isHost = !!(window.chrome && window.chrome.webview);
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

  const ICON = {
    play: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M7.5 4.6v14.8a1 1 0 0 0 1.52.85l12-7.4a1 1 0 0 0 0-1.7l-12-7.4A1 1 0 0 0 7.5 4.6Z" fill="currentColor"/></svg>',
    back: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M8 16 17 7M9.5 7H17v7.5" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"/></svg>',
    find: '<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="10.5" cy="10.5" r="6" fill="none" stroke="currentColor" stroke-width="2.3"/><path d="m15.2 15.2 4.6 4.6" stroke="currentColor" stroke-width="2.3" stroke-linecap="round"/></svg>',
    plus: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 5v14M5 12h14" stroke="currentColor" stroke-width="2.4" stroke-linecap="round"/></svg>',
    spinner: '<span class="spinner"></span>',
  };
  const PLAY = {
    ready: { icon: ICON.play, label: 'Jouer' },
    launching: { icon: ICON.spinner, label: 'Lancement…' },
    running: { icon: ICON.back, label: 'Revenir au jeu' },
    missing: { icon: ICON.find, label: 'Retrouver le jeu' },
    empty: { icon: ICON.plus, label: 'Ajouter un jeu' },
  };

  /* ================= Pont avec l'hôte ================= */

  function hostBridge() {
    let seq = 0;
    const pending = new Map();
    const listeners = {};
    window.chrome.webview.addEventListener('message', (e) => {
      const m = e.data;
      if (!m) return;
      if (m.id && pending.has(m.id)) {
        const p = pending.get(m.id);
        pending.delete(m.id);
        if (m.ok) p.resolve(m.result); else p.reject(new Error(m.error || 'Erreur inconnue'));
      } else if (m.event) {
        (listeners[m.event] || []).forEach((fn) => fn(m));
      }
    });
    return {
      demo: false,
      call(cmd, args) {
        return new Promise((resolve, reject) => {
          const id = ++seq;
          pending.set(id, { resolve, reject });
          window.chrome.webview.postMessage({ id, cmd, args: args || {} });
        });
      },
      on(ev, fn) { (listeners[ev] = listeners[ev] || []).push(fn); },
    };
  }

  /** Aperçu hors de l'app : mêmes commandes, données d'exemple, rien n'est lancé. */
  function demoBridge() {
    const listeners = {};
    const emit = (ev, data) => (listeners[ev] || []).forEach((fn) => fn(data));
    const hour = 3600e3;
    const art = new URLSearchParams(location.search).has('art');   // visuels locaux, pour les tests
    let games = [
      { id: 'pokopia', name: 'Pokémon Pokopia', subtitle: 'Port PC natif', exe: 'D:\\Jeux\\Pokopia\\pkp.exe', args: '--log pkp.log',
        image: art ? '../covers/pokopia.jpg' : null, exists: true, running: false, sessionStart: 0,
        seconds: 52320, last: new Date(Date.now() - 26 * hour).toISOString(), launches: 14 },
      { id: 'mkw', name: 'Mario Kart World', subtitle: 'Via Eglantine', exe: 'D:\\Emulateurs\\Eglantine\\eglantine.exe', args: '-g "D:\\Jeux\\mkw\\exefs\\main"',
        image: art ? '../covers/mkw.jpg' : null, exists: true, running: false, sessionStart: 0,
        seconds: 21900, last: new Date(Date.now() - 3 * hour).toISOString(), launches: 9 },
    ];
    games[0].update = null;
    games[1].update = {
      repo: 'Djihads80/Eglantine', channel: 'versions', installed: null, installedAt: null, canRestore: false, busy: false, available: true,
      latest: { tag: 'v0.1.0-rc1', name: 'Eglantine v0.1.0-rc1', publishedAt: new Date(Date.now() - 15 * hour).toISOString(),
        notes: 'Release candidate of 0.1.0. Please test it and report problems before the final release.\n\n- Initial release candidate',
        url: 'https://github.com/Djihads80/Eglantine/releases/tag/v0.1.0-rc1', file: 'eglantine-windows-x86_64-pgo.zip', size: 66202726 },
    };
    let demoCancel = false;
    const copy = () => games.map((g) => Object.assign({}, g));
    const find = (id) => games.find((g) => g.id === id);
    const api = {
      async init() { return { games: copy(), version: 'aperçu', maximized: false, nativeDrag: true }; },
      async ready() {},
      async launch(a) {
        await sleep(900);
        const g = find(a.id);
        if (!g) throw new Error('Ce jeu n\'est plus dans la collection.');
        if (g.running) return { state: 'already', games: copy() };
        g.running = true; g.sessionStart = Date.now(); g.last = new Date().toISOString(); g.launches++;
        setTimeout(() => { g.running = false; g.sessionStart = 0; g.seconds += 20; emit('state', { games: copy() }); }, 20000);
        return { state: 'started', games: copy() };
      },
      async focus() { return { ok: false }; },
      async openFolder() { throw new Error('Dans l\'aperçu, aucun dossier ne s\'ouvre.'); },
      async pickExe() { return { path: 'D:\\Jeux\\Exemple\\exemple.exe', name: 'Jeu d\'exemple' }; },
      async pickImage() { throw new Error('Dans l\'aperçu, on ne peut pas choisir d\'image.'); },
      async saveGame(a) {
        let g = find(a.id);
        if (!g) {
          g = { id: 'jeu-' + Date.now(), image: null, exists: true, running: false, sessionStart: 0, seconds: 0, last: null, launches: 0 };
          games.push(g);
        }
        Object.assign(g, { name: a.name.trim(), exe: a.exe.trim(), args: a.args.trim(), subtitle: a.subtitle.trim() });
        if (a.removeImage) g.image = null;
        const repo = (a.repo || '').trim().replace(/^.*github\.com\//i, '').replace(/\/+$/, '');
        g.update = repo ? Object.assign({ installed: null, installedAt: null, canRestore: false, busy: false, available: false, latest: null }, g.update || {}, { repo, channel: a.channel }) : null;
        return { id: g.id, games: copy() };
      },
      async removeGame(a) { games = games.filter((g) => g.id !== a.id); return { id: a.id, games: copy() }; },
      async checkUpdate(a) {
        await sleep(700);
        const g = find(a.id);
        if (g && g.update && !g.update.latest) throw new Error('Dans l\'aperçu, seule la mise à jour d\'exemple de Mario Kart World est simulée.');
        return { id: a.id, games: copy() };
      },
      async startUpdate(a) {
        const g = find(a.id), u = g.update, total = u.latest.size;
        demoCancel = false;
        u.busy = true;
        (async () => {
          const send = (phase, received) => emit('update', { id: g.id, phase, received, total, tag: u.latest.tag, message: null });
          for (let i = 0; i <= 28; i++) {
            if (demoCancel) { u.busy = false; send('cancelled', 0); return emit('state', { games: copy() }); }
            send('download', Math.round(total * i / 28));
            await sleep(120);
          }
          send('verify', total); await sleep(500);
          send('install', 0); await sleep(1100);
          Object.assign(u, { busy: false, installed: u.latest.tag, installedAt: new Date().toISOString(), available: false, canRestore: true });
          send('done', 0);
          emit('state', { games: copy() });
        })();
        return { id: a.id, games: copy() };
      },
      async cancelUpdate() { demoCancel = true; },
      async restoreUpdate(a) {
        await sleep(500);
        Object.assign(find(a.id).update, { installed: null, installedAt: null, canRestore: false, available: false });
        return { id: a.id, games: copy() };
      },
      async ignoreUpdate(a) { find(a.id).update.available = false; return { id: a.id, games: copy() }; },
      async openUrl() { throw new Error('Dans l\'aperçu, le lien ne s\'ouvre pas.'); },
      async window() {},
      async drag() {},
    };
    return {
      demo: true,
      call(cmd, args) { return api[cmd] ? api[cmd](args || {}) : Promise.reject(new Error('Commande inconnue')); },
      on(ev, fn) { (listeners[ev] = listeners[ev] || []).push(fn); },
    };
  }

  const bridge = isHost ? hostBridge() : demoBridge();

  /* ================= État ================= */

  const state = { games: [], selected: 0, launching: null, padName: null, nativeDrag: true };
  let cardsKey = null, shownArt = '', activeLayer = 'layerB', toastTimer = 0, draft = null;
  const tints = new Map();
  const launchedAt = {};   // jeu -> heure du lancement fait depuis le lanceur, pour repérer une fermeture immédiate
  let stateSeq = 0;
  const up = { id: null, checking: false, error: '', done: '', info: '' };   // fenêtre « Mises à jour »
  let job = null;                                                            // mise à jour en cours : { id, phase, received, total, tag }

  const current = () => state.games[state.selected] || null;

  function statusOf(g) {
    if (!g) return 'empty';
    if (!g.exists) return 'missing';
    if (state.launching === g.id) return 'launching';
    return g.running ? 'running' : 'ready';
  }

  /* ================= Textes ================= */

  function duration(seconds) {
    const m = Math.floor(Math.max(0, seconds) / 60);
    if (m < 1) return 'moins d\'une minute';
    if (m < 60) return m + ' min';
    const rest = m % 60;
    return Math.floor(m / 60) + ' h' + (rest ? ' ' + String(rest).padStart(2, '0') : '');
  }

  function lastPlayed(iso) {
    if (!iso) return null;
    const d = new Date(iso);
    if (isNaN(d)) return null;
    const midnight = (x) => new Date(x.getFullYear(), x.getMonth(), x.getDate()).getTime();
    const now = new Date();
    const days = Math.round((midnight(now) - midnight(d)) / 864e5);
    if (days <= 0) return 'aujourd\'hui';
    if (days === 1) return 'hier';
    if (days < 7) return 'il y a ' + days + ' jours';
    const opts = { day: 'numeric', month: 'long' };
    if (d.getFullYear() !== now.getFullYear()) opts.year = 'numeric';
    return 'le ' + d.toLocaleDateString('fr-FR', opts);
  }

  function sessionText(g) {
    const sec = g.sessionStart ? (Date.now() - g.sessionStart) / 1000 : 0;
    return sec >= 60 ? 'En cours · ' + duration(sec) : 'En cours';
  }

  function statusText(g, st) {
    if (st === 'running') return sessionText(g);
    if (st === 'launching') return 'Lancement…';
    if (st === 'missing') return 'Exécutable introuvable';
    return 'Prêt à jouer';
  }

  function metaOf(g) {
    const parts = [];
    if (g.subtitle) parts.push(g.subtitle);
    const when = lastPlayed(g.last);
    if (!when && g.seconds < 60) parts.push('Jamais lancé');
    else {
      if (when && !g.running) parts.push('Dernière partie ' + when);
      if (g.seconds >= 60) parts.push(duration(g.seconds) + ' de jeu');
    }
    return parts;
  }

  function cardSub(g, st) {
    if (st === 'running') return 'En cours';
    if (st === 'launching') return 'Lancement…';
    if (st === 'missing') return 'Introuvable';
    const when = lastPlayed(g.last);
    return when ? 'Joué ' + when : 'Jamais lancé';
  }

  /* ================= Visuels ================= */

  function hueOf(name) {
    let h = 7;
    for (const c of name || '') h = (h * 31 + c.codePointAt(0)) % 360;
    return h;
  }

  /** Met le visuel d'un jeu sur un élément : son image, sinon un dégradé tiré de son nom. */
  function applyArt(el, name, image) {
    if (image) {
      el.classList.remove('gen');
      el.style.backgroundImage = 'url("' + image + '")';
      el.removeAttribute('data-letter');
    } else {
      el.classList.add('gen');
      el.style.backgroundImage = '';
      el.style.setProperty('--h', hueOf(name));
      el.dataset.letter = (name || '').trim().charAt(0).toUpperCase();
    }
  }

  /** Couleur dominante du visuel : elle teinte le halo du fond et l'ombre du bouton Jouer. */
  function tintOf(g, done) {
    const fallback = () => {
      const h = hueOf(g.name) * Math.PI / 180;   // approximation suffisante pour un halo
      return [150 + 70 * Math.cos(h), 120 + 50 * Math.cos(h - 2.1), 130 + 70 * Math.cos(h + 2.1)].map(Math.round);
    };
    if (!g.image) return done(fallback());
    if (tints.has(g.image)) return done(tints.get(g.image));
    const img = new Image();
    img.onload = () => {
      let rgb;
      try {
        const c = document.createElement('canvas');
        c.width = c.height = 24;
        const ctx = c.getContext('2d');
        ctx.drawImage(img, 0, 0, 24, 24);
        const d = ctx.getImageData(0, 0, 24, 24).data;
        let r = 0, gr = 0, b = 0, w = 0;
        for (let i = 0; i < d.length; i += 4) {
          // les pixels saturés pèsent plus lourd : on veut la couleur du jeu, pas un gris moyen
          const s = (Math.max(d[i], d[i + 1], d[i + 2]) - Math.min(d[i], d[i + 1], d[i + 2])) / 255 + 0.05;
          r += d[i] * s; gr += d[i + 1] * s; b += d[i + 2] * s; w += s;
        }
        rgb = [r / w, gr / w, b / w].map(Math.round);
      } catch (e) { rgb = fallback(); }
      tints.set(g.image, rgb);
      done(rgb);
    };
    img.onerror = () => done(fallback());
    img.src = g.image;
  }

  function showArt(g) {
    const key = g ? g.id + '|' + g.image + '|' + g.name : 'vide';
    if (key === shownArt) return;
    shownArt = key;
    const next = activeLayer === 'layerA' ? 'layerB' : 'layerA';
    if (g) for (const el of $(next).children) applyArt(el, g.name, g.image);
    $(next).classList.toggle('is-on', !!g);
    $(activeLayer).classList.remove('is-on');
    activeLayer = next;
    if (g) tintOf(g, (rgb) => { if (current() === g) $('stage').style.setProperty('--tint', rgb.join(' ')); });
  }

  /* ================= Rendu ================= */

  function renderHero(animate) {
    const g = current();
    const st = statusOf(g);
    const hero = $('hero');
    hero.dataset.state = st;
    if (g) {
      $('statusText').textContent = statusText(g, st);
      $('title').textContent = g.name;
      $('title').classList.toggle('long', g.name.length > 24);
      $('meta').replaceChildren(...metaOf(g).map((t) => { const s = document.createElement('span'); s.textContent = t; return s; }));
    } else {
      $('title').textContent = 'Ta collection est vide';
      $('title').classList.remove('long');
      const s = document.createElement('span');
      s.textContent = 'Ajoute ton premier jeu pour commencer.';
      $('meta').replaceChildren(s);
    }
    $('playIcon').innerHTML = PLAY[st].icon;
    $('playLabel').textContent = PLAY[st].label;
    $('play').disabled = st === 'launching';

    const upd = g && g.update;
    $('menuUpdate').hidden = !upd;
    $('updatePill').hidden = !(upd && (jobActive(g) || (upd.available && upd.latest)));
    if (!$('updatePill').hidden) $('updatePillText').textContent = jobActive(g) ? jobText(true) : emuName(g) + ' ' + upd.latest.tag + ' disponible';
    showArt(g);
    if (animate) {
      hero.classList.remove('swap');
      void hero.offsetWidth;   // relance l'animation
      hero.classList.add('swap');
    }
  }

  function buildCards() {
    const wrap = $('cards');
    wrap.replaceChildren();
    state.games.forEach((g, i) => {
      const card = document.createElement('button');
      card.type = 'button';
      card.className = 'card';
      card.dataset.id = g.id;
      card.setAttribute('role', 'option');
      card.innerHTML = '<span class="thumb"></span><span class="card-name"></span><span class="card-sub"><i class="dot"></i><span></span></span>';
      applyArt(card.firstElementChild, g.name, g.image);
      card.querySelector('.card-name').textContent = g.name;
      card.addEventListener('click', () => select(i));
      card.addEventListener('dblclick', () => { select(i); play(); });
      wrap.append(card);
    });

    const add = document.createElement('button');
    add.type = 'button';
    add.className = 'card add';
    add.innerHTML = '<span class="thumb"><span class="plus">' + ICON.plus + '</span></span>'
      + '<span class="card-name">Ajouter un jeu</span><span class="card-sub"><span>Jeu PC ou émulé</span></span>';
    add.addEventListener('click', () => openEditor(null));
    wrap.append(add);

    const n = state.games.length;
    $('count').textContent = n === 0 ? 'Aucun jeu' : n + (n > 1 ? ' jeux' : ' jeu');
  }

  function renderCards() {
    const key = state.games.map((g) => g.id + '|' + g.name + '|' + g.image).join('\n');
    if (key !== cardsKey) { cardsKey = key; buildCards(); }
    document.querySelectorAll('.card[data-id]').forEach((card, i) => {
      const g = state.games[i];
      const st = statusOf(g);
      const on = i === state.selected;
      card.dataset.state = st;
      card.setAttribute('aria-selected', String(on));
      card.tabIndex = on ? 0 : -1;
      card.querySelector('.card-sub span').textContent = cardSub(g, st);
    });
  }

  function renderFoot() {
    const pad = state.padName;
    $('hints').hidden = state.games.length === 0;
    const a = pad && /dualsense|dualshock|054c/i.test(pad) ? '✕' : 'A';
    $('hints').innerHTML = pad
      ? '<span class="hint"><kbd class="pad">' + a + '</kbd>Jouer</span><span class="hint"><kbd>◀</kbd><kbd>▶</kbd>Choisir</span>'
      : '<span class="hint"><kbd>Entrée</kbd>Jouer</span><span class="hint"><kbd>←</kbd><kbd>→</kbd>Choisir</span>';

    const running = state.games.find((g) => g.running);
    $('now').hidden = !running;
    if (running) {
      const sec = running.sessionStart ? (Date.now() - running.sessionStart) / 1000 : 0;
      $('nowText').textContent = running.name + (sec >= 60 ? ' · en cours depuis ' + duration(sec) : ' · en cours');
      $('now').dataset.id = running.id;
    }
  }

  function render(animate) {
    renderHero(animate);
    renderCards();
    renderFoot();
  }

  /** Remplace la liste des jeux en gardant la sélection (ou en sélectionnant `wantedId`). */
  function setGames(games, wantedId) {
    const keep = wantedId || (current() && current().id);
    state.games = games || [];
    const i = state.games.findIndex((g) => g.id === keep);
    const before = state.selected;
    state.selected = i >= 0 ? i : Math.min(before, Math.max(0, state.games.length - 1));
    render(false);
  }

  function select(i, focus) {
    const n = state.games.length;
    if (!n) return;
    const next = Math.max(0, Math.min(n - 1, i));
    const changed = next !== state.selected;
    state.selected = next;
    closeMenu();
    render(changed);
    const card = document.querySelectorAll('.card[data-id]')[next];
    if (card) {
      if (focus) card.focus({ preventScroll: true });
      card.scrollIntoView({ block: 'nearest', inline: 'nearest', behavior: 'smooth' });
    }
    try { localStorage.setItem('lanceur.jeu', current().id); } catch (e) { /* stockage indisponible */ }
  }

  function toast(text, kind, ms) {
    const t = $('toast');
    t.hidden = true;
    void t.offsetWidth;
    t.textContent = text;
    t.dataset.kind = kind || 'ok';
    t.hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => { t.hidden = true; }, ms || 4600);
  }

  /* ================= Actions ================= */

  async function focusGame(g) {
    try {
      const r = await bridge.call('focus', { id: g.id });
      if (!r || !r.ok) {
        toast(bridge.demo ? 'Aperçu : aucun jeu n\'est réellement ouvert.' : 'La fenêtre de ' + g.name + ' est introuvable. Essaie Alt+Tab.', 'warn');
      }
    } catch (e) { toast(e.message, 'error'); }
  }

  /** Question oui / non dans une petite fenêtre. « Annuler » a le focus : Entrée ne lance rien par accident. */
  let confirmDone = null;
  function confirmDialog(title, text, yes) {
    return new Promise((resolve) => {
      $('confirmTitle').textContent = title;
      $('confirmText').textContent = text;
      $('confirmYes').textContent = yes || 'Lancer quand même';
      confirmDone = resolve;
      $('confirm').returnValue = '';
      $('confirm').showModal();
      $('confirmNo').focus();
    });
  }

  async function play() {
    const g = current();
    const st = statusOf(g);
    if (st === 'empty') return openEditor(null);
    if (st === 'launching') return;
    if (st === 'running') return focusGame(g);
    if (st === 'missing') {
      toast('L\'exécutable de ' + g.name + ' est introuvable. Indique son nouvel emplacement.', 'warn');
      return openEditor(g, true);
    }

    // Un autre jeu tourne déjà : on prévient avant de lancer, plutôt que de laisser le jeu planter au démarrage.
    const other = state.games.find((x) => x.running && x.id !== g.id);
    if (other) {
      const go = await confirmDialog(other.name + ' est encore ouvert',
        'Lancer ' + g.name + ' en même temps risque de manquer de mémoire : il peut se fermer dès le démarrage. '
        + 'Le plus sûr est de quitter ' + other.name + ' d\'abord.');
      if (!go) return;
    }

    state.launching = g.id;
    render(false);
    const t0 = Date.now();
    try {
      const r = await bridge.call('launch', { id: g.id });
      const seq = stateSeq;
      if (r.state === 'started') launchedAt[g.id] = Date.now();
      await sleep(Math.max(0, 900 - (Date.now() - t0)));   // laisse « Lancement… » visible un instant
      state.launching = null;
      if (stateSeq === seq) setGames(r.games); else render(false);   // un état plus récent est déjà arrivé
      if (r.state === 'missing') toast('L\'exécutable de ' + g.name + ' est introuvable.', 'error');
      else if (r.state === 'already') toast(g.name + ' était déjà ouvert.');
      else if (bridge.demo) toast('Aperçu : le lancement est simulé pendant 20 secondes.');
    } catch (e) {
      state.launching = null;
      render(false);
      toast(g.name + ' n\'a pas pu démarrer : ' + e.message, 'error', 8000);
    }
  }

  /** L'hôte signale un changement : jeu ouvert ou fermé, temps de jeu. */
  function onState(m) {
    stateSeq++;
    setGames(m.games);
    if ($('updater').open) renderUpdater();
    for (const g of state.games) {
      if (!launchedAt[g.id] || g.running) continue;
      const lived = Date.now() - launchedAt[g.id];
      delete launchedAt[g.id];
      if (lived < 15000) quickExit(g);
    }
  }

  /** Un jeu lancé d'ici s'est refermé en quelques secondes : on le dit, avec la cause la plus fréquente. */
  function quickExit(g) {
    const other = state.games.find((x) => x.running && x.id !== g.id);
    const text = other
      ? g.name + ' s\'est fermé juste après le lancement. ' + other.name + ' est encore ouvert : deux jeux en même temps peuvent manquer de mémoire. Ferme-le, puis relance.'
      : g.name + ' s\'est fermé juste après le lancement. Réessaie ; si ça recommence, regarde le journal du jeu.';
    toast(text, 'warn', 16000);
  }

  /* ----- Menu « … » ----- */

  function closeMenu() {
    if ($('menu').hidden) return;
    $('menu').hidden = true;
    $('more').setAttribute('aria-expanded', 'false');
    const rm = $('menu').querySelector('[data-act="remove"]');
    rm.classList.remove('confirm');
    rm.textContent = 'Retirer de la collection';
  }

  function toggleMenu() {
    if (!$('menu').hidden) return closeMenu();
    $('menu').hidden = false;
    $('more').setAttribute('aria-expanded', 'true');
    $('menu').querySelector('button').focus();
  }

  async function menuAction(btn) {
    const g = current();
    if (!g) return;
    const act = btn.dataset.act;
    if (act === 'remove' && !btn.classList.contains('confirm')) {
      // premier clic : on demande confirmation sur place
      btn.classList.add('confirm');
      btn.textContent = 'Confirmer le retrait';
      return;
    }
    closeMenu();
    try {
      if (act === 'edit') openEditor(g);
      else if (act === 'update') { if (g.update) openUpdater(g, true); }
      else if (act === 'folder') await bridge.call('openFolder', { id: g.id });
      else if (act === 'remove') {
        const r = await bridge.call('removeGame', { id: g.id });
        setGames(r.games);
        render(true);
        toast(g.name + ' est retiré de ta collection. Ses fichiers ne sont pas touchés.');
      }
    } catch (e) { toast(e.message, 'error'); }
  }

  /* ----- Fenêtre « Ajouter / Modifier » ----- */

  function refreshPreview() {
    const hasImage = !!draft.imageUrl && !draft.removeImage;
    applyArt($('fPreview'), $('fName').value || 'Jeu', hasImage ? draft.imageUrl : null);
    $('fClearImage').hidden = !hasImage;
    $('fPickImage').textContent = hasImage ? 'Changer d\'image…' : 'Choisir une image…';
  }

  function formError(text, focusId) {
    $('fError').textContent = text || '';
    $('fError').hidden = !text;
    if (text && focusId) $(focusId).focus();
  }

  function openEditor(g, toExe) {
    closeMenu();
    draft = { id: g ? g.id : '', imageUrl: g ? g.image : null, imageToken: '', removeImage: false };
    $('editorTitle').textContent = g ? 'Modifier le jeu' : 'Ajouter un jeu';
    $('editorSave').textContent = g ? 'Enregistrer' : 'Ajouter';
    $('fName').value = g ? g.name : '';
    $('fExe').value = g ? g.exe : '';
    $('fArgs').value = g ? g.args : '';
    $('fSubtitle').value = g ? g.subtitle : '';
    $('fRepo').value = g && g.update ? g.update.repo : '';
    $('fChannel').value = g && g.update && g.update.channel === 'builds' ? 'builds' : 'versions';
    formError('');
    refreshPreview();
    $('editor').showModal();
    (toExe ? $('fPickExe') : $('fName')).focus();
  }

  async function pickExe() {
    try {
      const r = await bridge.call('pickExe', { current: $('fExe').value });
      if (!r) return;
      $('fExe').value = r.path;
      if (!$('fName').value.trim()) { $('fName').value = r.name; refreshPreview(); }
      formError('');
    } catch (e) { formError(e.message); }
  }

  async function pickImage() {
    try {
      const r = await bridge.call('pickImage');
      if (!r) return;
      draft.imageToken = r.token;
      draft.imageUrl = r.url;
      draft.removeImage = false;
      refreshPreview();
    } catch (e) { formError(e.message); }
  }

  async function saveEditor(e) {
    e.preventDefault();
    const game = {
      id: draft.id, name: $('fName').value, exe: $('fExe').value, args: $('fArgs').value, subtitle: $('fSubtitle').value,
      imageToken: draft.imageToken, removeImage: draft.removeImage,
      repo: $('fRepo').value, channel: $('fChannel').value,
    };
    if (!game.name.trim()) return formError('Donne un nom au jeu.', 'fName');
    if (!game.exe.trim()) return formError('Choisis l\'exécutable du jeu ou de l\'émulateur.', 'fPickExe');
    try {
      const r = await bridge.call('saveGame', game);
      $('editor').close();
      const isNew = !draft.id;
      setGames(r.games, r.id);
      render(isNew);
      if (isNew) toast(game.name.trim() + ' rejoint ta collection.');
      // un dépôt GitHub est indiqué : on regarde tout de suite s'il y a une version à proposer
      const saved = state.games.find((x) => x.id === r.id);
      if (saved && saved.update && !saved.update.latest) bridge.call('checkUpdate', { id: r.id }).then((c) => setGames(c.games)).catch(() => {});
    } catch (err) { formError(err.message); }
  }

  /* ================= Mises à jour de l'émulateur ================= */

  const emuName = (g) => g.update.repo.split('/')[1] || 'l\'émulateur';
  const megas = (bytes) => Math.round((bytes || 0) / 1048576) + ' Mo';
  const jobActive = (g) => !!(job && g && job.id === g.id && ['download', 'verify', 'install'].includes(job.phase));

  function jobText(short) {
    if (job.phase === 'download') {
      const pct = job.total > 0 ? Math.floor(100 * job.received / job.total) : 0;
      return short ? 'Mise à jour… ' + pct + ' %' : 'Téléchargement : ' + megas(job.received) + ' sur ' + megas(job.total) + ' (' + pct + ' %)';
    }
    if (job.phase === 'verify') return short ? 'Vérification…' : 'Vérification du fichier…';
    return short ? 'Installation…' : 'Installation : l\'ancienne version est mise de côté…';
  }

  function fact(id, main, sub) {
    const dd = $(id);
    dd.textContent = main;
    if (sub) { const s = document.createElement('small'); s.textContent = sub; dd.append(s); }
  }

  function renderUpdater() {
    const g = state.games.find((x) => x.id === up.id);
    if (!g || !g.update) { if ($('updater').open) $('updater').close(); return; }
    const u = g.update, l = u.latest, busy = jobActive(g);
    const newer = !!l && l.tag !== u.installed;

    $('upTitle').textContent = 'Mises à jour de ' + emuName(g);
    fact('upInstalled', u.installed || 'Inconnue', u.installed ? (u.installedAt ? 'installée ' + lastPlayed(u.installedAt) : '') : 'installée à la main');
    fact('upLatest', up.checking ? 'Recherche…' : (l ? l.tag : 'Inconnue'),
      !up.checking && l ? [l.publishedAt ? 'publiée ' + lastPlayed(l.publishedAt) : '', l.size ? megas(l.size) : ''].filter(Boolean).join(' · ') : '');

    $('upNotesBox').hidden = !(l && l.notes) || busy;
    $('upNotes').textContent = l ? l.notes : '';

    $('upProgress').hidden = !busy;
    if (busy) {
      $('upProgress').classList.toggle('is-busy', job.phase !== 'download');
      $('upBar').style.width = (job.total > 0 ? 100 * job.received / job.total : 0) + '%';
      $('upPhase').textContent = jobText(false);
    }

    const finished = !!up.done && !busy;   // une installation vient de se terminer dans cette fenêtre
    const upToDate = !busy && !up.checking && !!l && !newer;
    const text = up.error || up.done || up.info
      || (upToDate ? emuName(g) + ' est à jour : ' + l.tag + (u.installedAt ? ', installée ' + lastPlayed(u.installedAt) : '') + '.' : '');
    $('upMessage').hidden = !text;
    $('upMessage').textContent = text;
    $('upMessage').dataset.kind = up.error ? 'error' : (up.info && !up.done ? 'info' : 'ok');
    $('upSafe').hidden = busy || up.checking || !newer || finished;

    const link = $('upRestore');
    link.hidden = busy || !(u.canRestore || u.available);
    link.dataset.mode = u.canRestore ? 'restore' : 'ignore';
    link.textContent = u.canRestore ? 'Remettre la version précédente' : 'Ignorer cette version';

    const primary = $('upPrimary'), secondary = $('upSecondary');
    primary.hidden = busy;
    primary.disabled = up.checking;
    primary.textContent = newer && !finished ? 'Installer ' + l.tag : 'Fermer';
    primary.dataset.mode = newer && !finished ? 'install' : 'close';
    secondary.hidden = finished;           // installation finie : il ne reste qu'à fermer
    secondary.disabled = up.checking || (busy && job.phase !== 'download');
    secondary.textContent = busy ? 'Annuler' : (newer ? 'Plus tard' : 'Vérifier à nouveau');
    secondary.dataset.mode = busy ? 'cancel' : (newer ? 'close' : 'check');
  }

  async function checkUpdate(g) {
    up.checking = true; up.error = ''; up.done = ''; up.info = '';
    renderUpdater();
    try { setGames((await bridge.call('checkUpdate', { id: g.id })).games); }
    catch (e) { up.error = e.message; }
    up.checking = false;
    renderUpdater();
  }

  function openUpdater(g, refresh) {
    closeMenu();
    up.id = g.id; up.error = ''; up.done = ''; up.info = '';
    renderUpdater();
    if (!$('updater').open) $('updater').showModal();
    if (refresh || !g.update.latest) checkUpdate(g);
  }

  async function upPrimaryClick() {
    const g = state.games.find((x) => x.id === up.id);
    if (!g || $('upPrimary').dataset.mode !== 'install') return $('updater').close();
    up.error = ''; up.done = ''; up.info = '';
    try {
      const r = await bridge.call('startUpdate', { id: g.id });
      job = { id: g.id, phase: 'download', received: 0, total: g.update.latest.size, tag: g.update.latest.tag };
      setGames(r.games);
    } catch (e) { up.error = e.message; }
    renderUpdater();
  }

  function upSecondaryClick() {
    const g = state.games.find((x) => x.id === up.id);
    const mode = $('upSecondary').dataset.mode;
    if (mode === 'cancel') bridge.call('cancelUpdate');
    else if (mode === 'check' && g) checkUpdate(g);
    else $('updater').close();
  }

  async function upLinkClick() {
    const g = state.games.find((x) => x.id === up.id);
    if (!g) return;
    try {
      if ($('upRestore').dataset.mode === 'restore') {
        const ok = await confirmDialog('Remettre la version précédente ?',
          'La version actuelle de ' + emuName(g) + ' sera retirée et l\'ancienne remise en place, avec ses réglages. Tes sauvegardes ne bougent pas.',
          'Remettre l\'ancienne');
        if (!ok) return;
        setGames((await bridge.call('restoreUpdate', { id: g.id })).games);
        up.error = ''; up.done = ''; up.info = 'La version précédente est de retour.';
      } else {
        setGames((await bridge.call('ignoreUpdate', { id: g.id })).games);
        $('updater').close();
        toast('Cette version ne te sera plus proposée. Tu peux toujours l\'installer depuis le menu « … ».');
      }
    } catch (e) { up.error = e.message; }
    if ($('updater').open) renderUpdater();
  }

  /** L'hôte donne des nouvelles de la mise à jour en cours. */
  function onUpdate(m) {
    const g = state.games.find((x) => x.id === m.id);
    const name = g && g.update ? emuName(g) : 'L\'émulateur';
    const watching = $('updater').open && up.id === m.id;
    if (m.phase === 'done') {
      job = null;
      if (up.id === m.id) { up.error = ''; up.done = 'C\'est fait : ' + name + ' ' + m.tag + ' est installé. Tu peux fermer et jouer.'; }
      if (!watching) toast(name + ' ' + m.tag + ' est installé.');
    } else if (m.phase === 'error') {
      job = null;
      if (up.id === m.id) up.error = m.message;
      if (!watching) toast('Mise à jour de ' + name + ' : ' + m.message, 'error', 12000);
    } else if (m.phase === 'cancelled') {
      job = null;
      if (up.id === m.id) up.info = 'Mise à jour annulée. Rien n\'a été modifié.';
    } else job = m;
    render(false);
    if ($('updater').open) renderUpdater();
  }

  /* ================= Clavier, manette, fenêtre ================= */

  function overlayOpen() { return $('editor').open || $('confirm').open || $('updater').open || !$('menu').hidden; }

  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && !$('menu').hidden) { closeMenu(); $('more').focus(); return; }
    if (overlayOpen() || e.altKey || e.ctrlKey || e.metaKey) return;
    if (e.key === 'ArrowRight') { e.preventDefault(); select(state.selected + 1, true); }
    else if (e.key === 'ArrowLeft') { e.preventDefault(); select(state.selected - 1, true); }
    else if (e.key === 'Enter') {
      const t = document.activeElement;
      // Entrée sur un autre bouton (menu, fenêtre, ajout) garde son action normale
      if (!t || t === document.body || t.id === 'play' || (t.classList.contains('card') && t.dataset.id)) {
        e.preventDefault();
        play();
      }
    }
  });

  document.addEventListener('pointerdown', (e) => {
    if (!$('menu').hidden && !e.target.closest('#moreWrap')) closeMenu();
  });

  // Manette : croix ou stick pour choisir, A (✕ sur DualSense) pour jouer.
  let padPrev = {}, padLoop = false;
  function firstPad() {
    return Array.from(navigator.getGamepads ? navigator.getGamepads() : []).find(Boolean) || null;
  }
  function pollPad() {
    const gp = firstPad();
    const name = gp ? gp.id : null;
    if (name !== state.padName) { state.padName = name; renderFoot(); }
    if (!gp) { padLoop = false; return; }
    if (document.hasFocus()) {
      const b = (i) => !!(gp.buttons[i] && gp.buttons[i].pressed);
      const now = { left: b(14) || gp.axes[0] < -0.6, right: b(15) || gp.axes[0] > 0.6, a: b(0), back: b(1) };
      if ($('confirm').open) {
        // Dans la question oui / non : gauche-droite change de bouton, A valide, B (rond) annule.
        const no = $('confirmNo'), yes = $('confirmYes'), el = document.activeElement;
        if ((now.left && !padPrev.left) || (now.right && !padPrev.right)) (el === no ? yes : no).focus();
        if (now.a && !padPrev.a && (el === no || el === yes)) el.click();
        if (now.back && !padPrev.back) $('confirm').close('');
      } else if (!overlayOpen()) {
        if (now.left && !padPrev.left) select(state.selected - 1, true);
        if (now.right && !padPrev.right) select(state.selected + 1, true);
        if (now.a && !padPrev.a) play();
      }
      padPrev = now;
    }
    requestAnimationFrame(pollPad);
  }
  function startPad() { if (!padLoop) { padLoop = true; requestAnimationFrame(pollPad); } }
  window.addEventListener('gamepadconnected', startPad);
  window.addEventListener('gamepaddisconnected', startPad);

  function tickClock() {
    $('clock').textContent = new Date().toLocaleTimeString('fr-FR', { hour: '2-digit', minute: '2-digit' });
  }

  function setMaximized(max) {
    document.body.classList.toggle('is-max', !!max);
    $('winToggle').setAttribute('aria-label', max ? 'Restaurer' : 'Agrandir');
  }

  /* ================= Démarrage ================= */

  async function start() {
    document.body.classList.toggle('is-host', isHost);
    $('winControls').hidden = !isHost;
    $('previewTag').hidden = isHost;

    $('play').addEventListener('click', play);
    $('more').addEventListener('click', toggleMenu);
    $('menu').addEventListener('click', (e) => { const b = e.target.closest('button'); if (b) menuAction(b); });
    $('nowBack').addEventListener('click', () => { const g = state.games.find((x) => x.id === $('now').dataset.id); if (g) focusGame(g); });
    $('winControls').addEventListener('click', (e) => { const b = e.target.closest('button'); if (b) bridge.call('window', { action: b.dataset.win }); });

    $('editorForm').addEventListener('submit', saveEditor);
    $('editorCancel').addEventListener('click', () => $('editor').close());
    $('editorClose').addEventListener('click', () => $('editor').close());
    $('fPickExe').addEventListener('click', pickExe);
    $('fPickImage').addEventListener('click', pickImage);
    $('fClearImage').addEventListener('click', () => { draft.removeImage = true; draft.imageToken = ''; refreshPreview(); });
    $('fName').addEventListener('input', () => { if (!draft.imageUrl || draft.removeImage) refreshPreview(); });

    $('confirmYes').addEventListener('click', () => $('confirm').close('oui'));
    $('confirmNo').addEventListener('click', () => $('confirm').close(''));
    $('confirm').addEventListener('close', () => {   // se déclenche aussi avec Échap, qui vaut « Annuler »
      const done = confirmDone;
      confirmDone = null;
      if (done) done($('confirm').returnValue === 'oui');
    });

    $('updatePill').addEventListener('click', () => { const g = current(); if (g && g.update) openUpdater(g, false); });
    $('upClose').addEventListener('click', () => $('updater').close());
    $('upPrimary').addEventListener('click', upPrimaryClick);
    $('upSecondary').addEventListener('click', upSecondaryClick);
    $('upRestore').addEventListener('click', upLinkClick);
    $('upLink').addEventListener('click', () => {
      const g = state.games.find((x) => x.id === up.id);
      if (g && g.update && g.update.latest) bridge.call('openUrl', { url: g.update.latest.url }).catch((e) => toast(e.message, 'warn'));
    });

    bridge.on('state', onState);
    bridge.on('update', onUpdate);
    bridge.on('window', (m) => setMaximized(m.maximized));

    tickClock();
    setInterval(() => { tickClock(); if (state.games.some((g) => g.running)) render(false); }, 15000);

    let init;
    try { init = await bridge.call('init'); }
    catch (e) { toast('Impossible de lire ta collection : ' + e.message, 'error', 12000); init = { games: [] }; }

    state.nativeDrag = init.nativeDrag !== false;
    setMaximized(init.maximized);
    let wanted = null;
    try { wanted = localStorage.getItem('lanceur.jeu'); } catch (e) { /* stockage indisponible */ }
    setGames(init.games, wanted);
    if (init.warning) toast(init.warning, 'warn', 12000);
    if (firstPad()) startPad();

    // Secours pour un vieux moteur sans déplacement natif : on demande à l'hôte de déplacer la fenêtre.
    if (isHost && !state.nativeDrag) {
      $('titlebar').addEventListener('mousedown', (e) => { if (e.button === 0 && !e.target.closest('button')) bridge.call('drag'); });
      $('titlebar').addEventListener('dblclick', (e) => { if (!e.target.closest('button')) bridge.call('window', { action: 'toggle' }); });
    }

    if (document.fonts && document.fonts.ready) await document.fonts.ready;
    bridge.call('ready');
  }

  start();
})();
