(() => {
  const REFRESH_MS = 30000;
  const cfg = window.ROOM_CFG;
  const $ = id => document.getElementById(id);
  const fmtTime = d => d.toLocaleTimeString('es-ES', { hour: '2-digit', minute: '2-digit' });
  const token = () => document.querySelector('#antiforgery input[name="__RequestVerificationToken"]').value;

  let selectedMinutes = 30;
  let bookModal;
  let lastStatus = null;

  // ---- Reloj local (cada segundo) ----
  function tickClock() {
    const now = new Date();
    $('time').textContent = fmtTime(now);
    $('date').textContent = now.toLocaleDateString('es-ES', { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' });
  }
  setInterval(tickClock, 1000); tickClock();

  // ---- Estado de la sala ----
  async function refresh() {
    try {
      const r = await fetch('/api/status', { cache: 'no-store' });
      if (!r.ok) throw new Error(r.status);
      render(await r.json());
    } catch (e) {
      showError('Sin conexión con el servidor. Reintentando…');
    }
  }

  function showError(msg) {
    const el = $('errorBar');
    el.textContent = msg; el.classList.toggle('d-none', !msg);
  }

  function render(s) {
    lastStatus = s;
    showError(s.error || '');
    const app = $('app');
    app.classList.toggle('busy', s.isBusy);
    app.classList.toggle('free', !s.isBusy);
    $('statusLabel').textContent = s.isBusy ? 'OCUPADA' : 'LIBRE';

    const cur = s.current;
    $('currentCard').classList.toggle('d-none', !cur);
    if (cur) {
      $('statusSub').textContent = 'Libre a las ' + fmtTime(utc(cur.endUtc));
      $('curTitle').textContent = cur.subject;
      $('curTime').textContent = fmtTime(utc(cur.startUtc)) + ' – ' + fmtTime(utc(cur.endUtc));
      $('curOrg').textContent = cur.organizer ? 'Organiza: ' + cur.organizer : '';
    } else {
      $('statusSub').textContent = s.freeUntilUtc
        ? 'Libre hasta las ' + fmtTime(utc(s.freeUntilUtc))
        : 'Libre el resto del día';
    }

    const ul = $('upcoming'); ul.innerHTML = '';
    (s.upcoming || []).forEach(m => {
      const li = document.createElement('li');
      const t = document.createElement('div'); t.className = 'u-time';
      t.textContent = fmtTime(utc(m.startUtc)) + ' – ' + fmtTime(utc(m.endUtc));
      const n = document.createElement('div'); n.className = 'u-title'; n.textContent = m.subject;
      li.append(t, n);
      if (m.organizer) { const o = document.createElement('div'); o.className = 'u-org'; o.textContent = m.organizer; li.append(o); }
      ul.append(li);
    });
    $('noUpcoming').classList.toggle('d-none', (s.upcoming || []).length > 0);
    $('updated').textContent = fmtTime(new Date());
  }

  const utc = iso => new Date(/[zZ]|[+-]\d\d:?\d\d$/.test(iso) ? iso : iso + 'Z');

  // ---- Formulario de reserva ----
  function buildStarts() {
    const sel = $('startSel'); sel.innerHTML = '';
    const now = new Date();
    const [wsH, wsM] = cfg.workStart.split(':').map(Number);
    const [weH, weM] = cfg.workEnd.split(':').map(Number);
    const open = new Date(now); open.setHours(wsH, wsM, 0, 0);
    const close = new Date(now); close.setHours(weH, weM, 0, 0);

    const optNow = new Option('Ahora (' + fmtTime(now) + ')', 'now'); sel.add(optNow);
    let t = new Date(Math.max(now.getTime(), open.getTime()));
    t.setSeconds(0, 0);
    t.setMinutes(Math.ceil((t.getMinutes() + (t > now ? 0 : 1)) / 15) * 15);
    for (; t < close; t = new Date(t.getTime() + 15 * 60000)) {
      sel.add(new Option(fmtTime(t), t.toISOString()));
    }
  }

  function buildDurations() {
    const box = $('durations'); box.innerHTML = '';
    [15, 30, 45, 60, 90, 120, 180, 240].filter(m => m >= cfg.min && m <= cfg.max).forEach(m => {
      const b = document.createElement('button');
      b.type = 'button'; b.className = 'btn btn-dur ' + (m === selectedMinutes ? 'btn-primary' : 'btn-outline-primary');
      b.textContent = m < 60 ? m + ' min' : (m / 60) + ' h' + (m % 60 ? ' ' + (m % 60) + ' min' : '');
      b.onclick = () => { selectedMinutes = m; buildDurations(); };
      box.append(b);
    });
  }

  function msg(text, kind) {
    const el = $('bookMsg'); el.className = 'alert alert-' + kind; el.textContent = text;
  }

  $('btnBook').addEventListener('click', () => {
    bookModal = bookModal || new bootstrap.Modal($('bookModal'));
    $('title').value = ''; $('organizer').value = '';
    $('bookMsg').className = 'alert d-none';
    selectedMinutes = Math.min(Math.max(30, cfg.min), cfg.max);
    buildStarts(); buildDurations();
    bookModal.show();
  });

  $('btnConfirm').addEventListener('click', async () => {
    const title = $('title').value.trim();
    if (title.length < 2) { msg('Escribe un título para la reunión.', 'warning'); return; }
    const v = $('startSel').value;
    const startUtc = v === 'now' ? new Date().toISOString() : v;

    const btn = $('btnConfirm'); btn.disabled = true; btn.textContent = 'Reservando…';
    try {
      const r = await fetch('/api/book', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token() },
        body: JSON.stringify({ title, organizer: $('organizer').value.trim(), startUtc, minutes: selectedMinutes })
      });
      const data = await r.json().catch(() => ({}));
      if (r.ok && data.success) {
        msg('✔ Reserva confirmada', 'success');
        refresh();
        setTimeout(() => bookModal.hide(), 1500);
      } else {
        msg(data.message || 'No se pudo completar la reserva.', 'danger');
      }
    } catch (e) {
      msg('Error de conexión. Inténtalo de nuevo.', 'danger');
    } finally {
      btn.disabled = false; btn.textContent = 'Confirmar reserva';
    }
  });

  refresh();
  setInterval(refresh, REFRESH_MS);
})();
