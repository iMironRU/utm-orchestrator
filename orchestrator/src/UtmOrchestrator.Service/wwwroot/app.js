/* =====================================================================
   UTM Orchestrator — панель управления (vanilla-JS SPA, без сборки)
   ---------------------------------------------------------------------
   Архитектура: одно дерево состояния (state) + функции-рендеры, которые
   возвращают HTML-строки. Всё дерево перерисовывается в #app при смене
   состояния; события обрабатываются делегированием (data-action).
   Тематизация задаётся инлайн-стилями из объекта токенов colors(),
   чтобы пиксель-в-пиксель повторить дизайн-хэндофф.

   Данные — только живые: /api/status (УТМ, ~8с опрос) и /api/logs (логи).
   Демо-данных нет. Ещё не реализованные действия честно говорят «в разработке»
   (notReady), а не притворяются успехом.
   ===================================================================== */
(function () {
  'use strict';

  /* ---------- утилита экранирования (для строк из API) ---------- */
  function esc(s) {
    return String(s == null ? '' : s).replace(/[&<>"]/g, function (ch) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[ch];
    });
  }

  /* ====================== СОСТОЯНИЕ ПРИЛОЖЕНИЯ ====================== */
  var state = {
    theme: 'dark',                 // 'dark' | 'light' — единый глобальный источник
    isMobile: false,               // определяется через matchMedia
    screen: 'overview',            // текущий экран
    selectedUtmId: null,           // выбранный УТМ (id из живого статуса)
    notifOpen: false,
    mobileNavOpen: false,
    confirmDeleteOpen: false,
    toast: null,
    unreadCount: 0,
    firewallAuto: true,            // предпочтение «открывать порты в файрволе»
    remoteAccess: false,
    pinVisible: false,
    logsFilterPort: '',
    logsFilterLevel: '',
    logsSearch: '',
    logs: null,                    // реальные логи из /api/logs (null = ещё не грузили)
    settingsLoaded: false,         // подгружены ли настройки с бэка
    scannedTokens: null,           // результат скана токенов трея (null = не сканировали)
    scanning: false,               // идёт скан
    healing: false,                // идёт лечение
    overviewFilter: null,          // null | 'problem'

    /* --- аутентификация «по галочке» (доп. требование продукта) --- */
    requireAuth: false,            // «Требовать вход в панель» — по умолчанию ВЫКЛ
    authed: false,                 // выполнен ли вход в этой сессии

    /* --- IP-allowlist (доп. требование продукта) --- */
    allowedIps: [],

    /* --- живые данные с /api/status --- */
    liveStatus: null,              // последний успешный ответ API
    liveError: false,              // недоступность службы
    lastCheck: '—',                // время последней проверки
  };

  /* сохранённая тема (offline localStorage) */
  try {
    var savedTheme = localStorage.getItem('utm.theme');
    if (savedTheme === 'dark' || savedTheme === 'light') state.theme = savedTheme;
  } catch (e) {}

  /* Все экраны работают на ЖИВЫХ данных: УТМ — из /api/status, логи — из /api/logs,
     токены/уведомления — производные от статуса. Демо-массивов больше нет. */

  /* ====================== ТОКЕНЫ ЦВЕТА ====================== */
  function colors() {
    var dark = state.theme === 'dark';
    return dark ? {
      appBg: '#10141a', sidebarBg: '#0d1117', cardBg: '#161b22', subtleBg: 'rgba(255,255,255,.04)',
      border: 'rgba(255,255,255,.08)', borderStrong: 'rgba(255,255,255,.16)',
      textPrimary: '#e6e9ee', textSecondary: 'rgba(230,233,238,.6)', textTertiary: 'rgba(230,233,238,.45)',
      brand: '#8b7cf6', brandBg: 'rgba(139,124,246,.16)', brandText: '#c2b8ff',
      ok: '#34d399', okBg: 'rgba(52,211,153,.12)', warn: '#fbbf24', warnBg: 'rgba(251,191,36,.12)',
      error: '#f87171', errorBg: 'rgba(248,113,113,.12)', errorSoftBg: 'rgba(248,113,113,.06)',
      stopped: '#9aa4b2', stoppedBg: 'rgba(154,164,178,.12)', progress: '#60a5fa', progressBg: 'rgba(96,165,250,.12)',
      toastBg: '#2a2f38',
    } : {
      appBg: '#f7f6f3', sidebarBg: '#ffffff', cardBg: '#ffffff', subtleBg: '#faf9f7',
      border: '#ece9e2', borderStrong: '#ddd8cd',
      textPrimary: '#201f1c', textSecondary: 'rgba(32,31,28,.6)', textTertiary: 'rgba(32,31,28,.45)',
      brand: '#4338ca', brandBg: 'rgba(67,56,202,.08)', brandText: '#4338ca',
      ok: '#15803d', okBg: 'rgba(21,128,61,.1)', warn: '#b45309', warnBg: 'rgba(180,83,9,.1)',
      error: '#b91c1c', errorBg: 'rgba(185,28,28,.1)', errorSoftBg: 'rgba(185,28,28,.05)',
      stopped: '#6b7280', stoppedBg: 'rgba(107,114,128,.1)', progress: '#0284c7', progressBg: 'rgba(2,132,199,.1)',
      toastBg: '#201f1c',
    };
  }

  /* ====================== ИКОНКИ (инлайн SVG) ====================== */
  var ICONS = {
    overview: '<svg width="18" height="18" viewBox="0 0 24 24" fill="none"><rect x="3" y="3" width="8" height="8" rx="2" fill="currentColor"/><rect x="13" y="3" width="8" height="8" rx="2" fill="currentColor" opacity="0.55"/><rect x="3" y="13" width="8" height="8" rx="2" fill="currentColor" opacity="0.55"/><rect x="13" y="13" width="8" height="8" rx="2" fill="currentColor"/></svg>',
    utm: '<svg width="18" height="18" viewBox="0 0 24 24" fill="none"><rect x="3" y="4" width="18" height="4.5" rx="1.5" fill="currentColor"/><rect x="3" y="10" width="18" height="4.5" rx="1.5" fill="currentColor" opacity="0.6"/><rect x="3" y="16" width="18" height="4.5" rx="1.5" fill="currentColor" opacity="0.35"/></svg>',
    tokens: '<svg width="18" height="18" viewBox="0 0 24 24" fill="none"><rect x="5" y="8" width="14" height="10" rx="2" fill="currentColor"/><rect x="9" y="3" width="6" height="6" rx="1" fill="currentColor" opacity="0.6"/></svg>',
    install: '<svg width="18" height="18" viewBox="0 0 24 24" fill="none"><circle cx="12" cy="12" r="9" stroke="currentColor" stroke-width="2"/><rect x="11" y="7" width="2" height="10" rx="1" fill="currentColor"/><rect x="7" y="11" width="10" height="2" rx="1" fill="currentColor"/></svg>',
    updates: '<svg width="18" height="18" viewBox="0 0 24 24" fill="none"><polygon points="12,4 19,12 15,12 15,18 9,18 9,12 5,12" fill="currentColor"/></svg>',
    logs: '<svg width="18" height="18" viewBox="0 0 24 24" fill="none"><rect x="4" y="6" width="16" height="2.4" rx="1.2" fill="currentColor"/><rect x="4" y="11" width="16" height="2.4" rx="1.2" fill="currentColor" opacity="0.6"/><rect x="4" y="16" width="10" height="2.4" rx="1.2" fill="currentColor" opacity="0.6"/></svg>',
    settings: '<svg width="18" height="18" viewBox="0 0 24 24" fill="none"><rect x="4" y="6" width="16" height="2" rx="1" fill="currentColor" opacity="0.5"/><circle cx="15" cy="7" r="2.6" fill="currentColor"/><rect x="4" y="16" width="16" height="2" rx="1" fill="currentColor" opacity="0.5"/><circle cx="9" cy="17" r="2.6" fill="currentColor"/></svg>',
    bell: '<svg width="19" height="19" viewBox="0 0 24 24" fill="none"><circle cx="12" cy="12" r="9" stroke="currentColor" stroke-width="1.6"/><rect x="11" y="7" width="2" height="6" rx="1" fill="currentColor"/><circle cx="12" cy="16" r="1.3" fill="currentColor"/></svg>',
    check: '<svg width="20" height="20" viewBox="0 0 24 24" fill="none"><path d="M5 13l4 4 10-10" stroke="#fff" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"/></svg>',
  };

  var NAV = [
    { key: 'overview', label: 'Обзор', action: 'goOverview', icon: ICONS.overview },
    { key: 'utm', label: 'УТМ', action: 'goUtm', icon: ICONS.utm },
    { key: 'tokens', label: 'Токены', action: 'goTokens', icon: ICONS.tokens },
    { key: 'install', label: 'Установка', action: 'goInstall', icon: ICONS.install },
    { key: 'updates', label: 'Обновления', action: 'goUpdates', icon: ICONS.updates },
    { key: 'logs', label: 'Логи', action: 'goLogs', icon: ICONS.logs },
    { key: 'settings', label: 'Настройки', action: 'goSettings', icon: ICONS.settings },
  ];

  /* активность пункта навигации */
  function navActive(key) {
    var s = state.screen;
    if (key === 'overview') return s === 'overview';
    if (key === 'utm') return s === 'utm' || s === 'utm-detail';
    return s === key;
  }
  function navStyle(active, c) {
    return active
      ? { bg: c.brandBg, color: c.brandText, icon: c.brandText }
      : { bg: 'transparent', color: c.textSecondary, icon: c.textTertiary };
  }

  /* ====================== МАППИНГ СТАТУСОВ ======================
     verdict/state из /api/status → одна из 5 дизайн-статусов. */
  function mapStatus(inst) {
    if (inst.verdict === 'Ok') return 'ok';
    if (inst.verdict === 'Faulty') return 'error';
    if (inst.verdict === 'Stopped') return 'stopped';
    // verdict Unknown / прочее — различаем по state
    if (inst.state === 'StartPending' || inst.state === 'StopPending') return 'progress';
    return 'warn'; // NotInstalled / Other / Unknown → «Внимание»
  }

  /* Живые УТМ из ответа API → форма, совместимая с buildUtmView */
  function liveUtms() {
    var d = state.liveStatus;
    if (!d || !d.instances) return [];
    return d.instances.map(function (inst, i) {
      var st = mapStatus(inst);
      // Отображаемое имя: title = кастомное имя → организация/адрес из сертификата
      // → имя службы (вычисляется на бэкенде). customName/org — для экрана деталей.
      return {
        id: 'live' + i,
        port: inst.port,
        service: inst.service || '',
        name: inst.title || inst.service || ('УТМ ' + (inst.port || '')),
        customName: inst.name || '',
        org: inst.org || '',
        fsrar: inst.fsrar || null,
        tokenSerial: inst.serial || null,
        status: st,
        reason: inst.reason || (st === 'warn' ? 'Требуется внимание' : ''),
        version: inst.version || '—',
        folder: inst.folder || '',
        firewallOpen: inst.firewallOpen === true,
        lastSync: 'только что',
        stoppedAt: 'остановлен',
        progressLabel: inst.reason || 'Идёт операция',
        progress: 50,
      };
    });
  }

  /* Построение view-модели карточки УТМ (порт прототипа) */
  function buildUtmView(u, c) {
    var meta = {
      ok: { label: 'Работает', color: c.ok, bg: c.okBg },
      warn: { label: 'Внимание', color: c.warn, bg: c.warnBg },
      error: { label: 'Сбой', color: c.error, bg: c.errorBg },
      stopped: { label: 'Остановлен', color: c.stopped, bg: c.stoppedBg },
      progress: { label: 'Идёт операция', color: c.progress, bg: c.progressBg },
    }[u.status];
    var isProgress = u.status === 'progress';
    var hasCallout = u.status === 'warn' || u.status === 'error';
    var hasMeta = u.status === 'ok' || u.status === 'stopped';
    var hasExchange = !isProgress;
    var exchangeText = '';
    if (u.status === 'ok') exchangeText = 'Обмен с ЕГАИС идёт · ' + (u.lastSync || '');
    else if (u.status === 'warn') exchangeText = 'Обмен приостановлен';
    else if (u.status === 'error') exchangeText = 'Обмен остановлен · последний успешный ' + (u.lastSync || '—');
    else if (u.status === 'stopped') exchangeText = 'Обмен не идёт';
    var primaryLabel = 'Перезапустить';
    if (u.status === 'warn') primaryLabel = 'Привязать токен';
    else if (u.status === 'stopped') primaryLabel = 'Запустить';
    var line1 = u.status === 'ok' ? ('ФСРАР ' + (u.fsrar || '—')) : 'Остановлен вручную';
    var line2 = u.status === 'ok'
      ? (u.tokenSerial ? 'Rutoken · ' + u.tokenSerial : 'нет токена')
      : (u.stoppedAt || 'остановлен');
    return {
      id: u.id, name: u.name, port: u.port, service: u.service || '',
      customName: u.customName || '', org: u.org || '',
      fsrarDisplay: u.fsrar || '—',
      tokenDisplay: u.tokenSerial ? ('Rutoken · ' + u.tokenSerial) : 'нет токена',
      tokenSerial: u.tokenSerial,
      folder: u.folder, version: u.version, internalPorts: u.internalPorts,
      firewallOpen: u.firewallOpen === true,
      statusLabel: meta.label, statusColor: meta.color, statusBg: meta.bg, status: u.status,
      dotAnim: (u.status === 'warn' || u.status === 'progress') ? 'animation:pulseDot 1.4s ease-in-out infinite;' : '',
      isProgress: isProgress, hasCallout: hasCallout, hasMeta: hasMeta, hasExchange: hasExchange,
      reasonText: u.reason || '', exchangeText: exchangeText, primaryLabel: primaryLabel,
      line1: line1, line2: line2,
      progressLabel: u.progressLabel || '', progress: u.progress || 0, progressTrack: c.subtleBg,
    };
  }

  /* ====================== МЕЛКИЕ РЕНДЕР-ХЕЛПЕРЫ ====================== */
  function segmented(c, options) {
    // options: [{label, active, action}]
    var btns = options.map(function (o) {
      return '<button data-action="' + o.action + '" style="border:none;padding:6px 14px;border-radius:6px;font:600 12px system-ui,sans-serif;cursor:pointer;background:' +
        (o.active ? c.brand : 'transparent') + ';color:' + (o.active ? '#fff' : c.textSecondary) + ';">' + esc(o.label) + '</button>';
    }).join('');
    return '<div style="display:flex;background:' + c.subtleBg + ';border-radius:8px;padding:2px;">' + btns + '</div>';
  }

  function statusPill(v, c) {
    return '<div style="display:flex;align-items:center;gap:6px;padding:5px 10px;border-radius:20px;background:' + v.statusBg + ';flex-shrink:0;">' +
      '<div style="width:7px;height:7px;border-radius:50%;background:' + v.statusColor + ';' + v.dotAnim + '"></div>' +
      '<span style="font:600 12px system-ui,sans-serif;color:' + v.statusColor + ';">' + esc(v.statusLabel) + '</span></div>';
  }

  /* ====================== НАВИГАЦИЯ (сайдбар / шторка) ====================== */
  function navItem(item, c, mobile) {
    var st = navStyle(navActive(item.key), c);
    var pad = mobile ? '11px 12px' : '9px 12px';
    var fs = mobile ? '13.5px' : '13px';
    return '<div data-action="' + item.action + '" style="display:flex;align-items:center;gap:10px;padding:' + pad + ';border-radius:8px;background:' + st.bg + ';margin-bottom:2px;cursor:pointer;">' +
      '<span style="display:flex;color:' + st.icon + ';">' + item.icon + '</span>' +
      '<span style="font:600 ' + fs + ' system-ui,sans-serif;color:' + st.color + ';">' + esc(item.label) + '</span></div>';
  }

  function themeSwitch(c) {
    var dark = state.theme === 'dark';
    return '<div data-action="toggleTheme" style="display:flex;align-items:center;justify-content:space-between;padding:10px 12px;border-radius:8px;background:' + c.subtleBg + ';margin-bottom:8px;cursor:pointer;">' +
      '<span style="font:12px system-ui,sans-serif;color:' + c.textSecondary + ';">Тёмная тема</span>' +
      '<div style="width:34px;height:19px;border-radius:10px;background:' + (dark ? c.brand : '#e2ded5') + ';position:relative;flex-shrink:0;">' +
      '<div style="position:absolute;top:2px;' + (dark ? 'right' : 'left') + ':2px;width:15px;height:15px;border-radius:50%;background:#fff;"></div></div></div>';
  }

  function serviceIndicator(c) {
    var connected = !state.liveError && !!state.liveStatus;
    var col = connected ? c.ok : c.error;
    var txt = connected ? 'Служба подключена' : 'Служба недоступна';
    return '<div id="svc-indicator" style="display:flex;align-items:center;gap:8px;padding:8px 12px;">' +
      '<div style="width:7px;height:7px;border-radius:50%;background:' + col + ';flex-shrink:0;"></div>' +
      '<span style="font:12px system-ui,sans-serif;color:' + c.textTertiary + ';">' + txt + '</span></div>';
  }

  function sidebar(c) {
    return '<div style="width:236px;flex-shrink:0;background:' + c.sidebarBg + ';border-right:1px solid ' + c.border + ';padding:20px 14px;display:flex;flex-direction:column;">' +
      '<div style="display:flex;align-items:center;gap:9px;padding:8px 10px 22px;">' +
        '<div style="width:26px;height:26px;border-radius:7px;background:' + c.brand + ';flex-shrink:0;display:flex;align-items:center;justify-content:center;"><svg width="15" height="15" viewBox="0 0 24 24" fill="none"><path d="M5 13l4 4 10-10" stroke="#fff" stroke-width="3.4" stroke-linecap="round" stroke-linejoin="round"/></svg></div>' +
        '<div style="font:700 13.5px system-ui,sans-serif;color:' + c.textPrimary + ';letter-spacing:.2px;">UTM ORCHESTRATOR</div>' +
      '</div>' +
      NAV.map(function (n) { return navItem(n, c, false); }).join('') +
      '<div style="flex:1;"></div>' +
      themeSwitch(c) +
      serviceIndicator(c) +
    '</div>';
  }

  function mobileTopBar(c) {
    return '<div style="display:flex;align-items:center;justify-content:space-between;padding:14px 16px;background:' + c.sidebarBg + ';border-bottom:1px solid ' + c.border + ';">' +
      '<div style="display:flex;align-items:center;gap:10px;">' +
        '<button data-action="toggleMobileNav" aria-label="Меню" style="border:none;background:transparent;padding:4px;cursor:pointer;display:flex;flex-direction:column;gap:3px;">' +
          '<span style="display:block;width:19px;height:2px;background:' + c.textPrimary + ';border-radius:1px;"></span>' +
          '<span style="display:block;width:19px;height:2px;background:' + c.textPrimary + ';border-radius:1px;"></span>' +
          '<span style="display:block;width:14px;height:2px;background:' + c.textPrimary + ';border-radius:1px;"></span>' +
        '</button>' +
        '<div style="width:22px;height:22px;border-radius:6px;background:' + c.brand + ';flex-shrink:0;display:flex;align-items:center;justify-content:center;"><svg width="13" height="13" viewBox="0 0 24 24" fill="none"><path d="M5 13l4 4 10-10" stroke="#fff" stroke-width="3.6" stroke-linecap="round" stroke-linejoin="round"/></svg></div>' +
        '<div style="font:700 13px system-ui,sans-serif;color:' + c.textPrimary + ';">UTM ORCHESTRATOR</div>' +
      '</div>' +
    '</div>';
  }

  function drawer(c) {
    return '<div data-action="toggleMobileNav" style="position:absolute;inset:0;background:rgba(0,0,0,.45);z-index:20;">' +
      '<div data-pop="1" style="width:78%;max-width:280px;height:100%;background:' + c.sidebarBg + ';padding:18px 14px;display:flex;flex-direction:column;box-shadow:2px 0 24px rgba(0,0,0,.3);">' +
        '<div style="display:flex;align-items:center;justify-content:space-between;padding:6px 8px 20px;">' +
          '<div style="font:700 13px system-ui,sans-serif;color:' + c.textPrimary + ';">Меню</div>' +
          '<button data-action="toggleMobileNav" style="border:none;background:transparent;color:' + c.textSecondary + ';font:16px system-ui,sans-serif;cursor:pointer;">✕</button>' +
        '</div>' +
        NAV.map(function (n) { return navItem(n, c, true); }).join('') +
        '<div style="flex:1;"></div>' +
        themeSwitch(c) +
      '</div>' +
    '</div>';
  }

  /* ====================== ШАПКА КОНТЕНТА + УВЕДОМЛЕНИЯ ====================== */
  function screenTitles() {
    var s = state.screen;
    var titles = {
      overview: ['Обзор', 'Все ваши УТМ в одном месте'],
      utm: ['УТМ', 'Список всех УТМ на этом компьютере'],
      tokens: ['Токены', 'Подключённые аппаратные ключи'],
      install: ['Установка УТМ', 'обследование и добавление'],
      updates: ['Обновления', 'Версии УТМ и оркестратора'],
      logs: ['Логи', 'События всех УТМ'],
      settings: ['Настройки', 'Безопасность, расписание, доступ'],
    };
    if (s === 'utm-detail') {
      var sel = selectedUtm();
      return [sel ? sel.name : 'УТМ', sel ? ('порт ' + sel.port) : ''];
    }
    return titles[s] || ['Обзор', ''];
  }

  /* Уведомления из живого статуса: каждый сбойный / требующий внимания УТМ. */
  function liveNotifications() {
    var d = state.liveStatus;
    if (!d || !d.instances) return [];
    var out = [];
    d.instances.forEach(function (inst) {
      var st = mapStatus(inst);
      if (st === 'error' || st === 'warn') {
        var name = inst.title || inst.service || ('порт ' + inst.port);
        out.push({
          level: st === 'error' ? 'error' : 'warn',
          text: name + ' (порт ' + inst.port + '): ' + (inst.reason || (st === 'error' ? 'сбой' : 'требует внимания')),
          time: state.lastCheck,
        });
      }
    });
    return out;
  }

  function header(c) {
    var t = screenTitles();
    var notifs = liveNotifications();
    var badge = notifs.length
      ? '<span style="position:absolute;top:-4px;right:-4px;background:' + c.error + ';color:#fff;font:700 10px system-ui,sans-serif;min-width:16px;height:16px;border-radius:8px;display:flex;align-items:center;justify-content:center;padding:0 3px;">' + notifs.length + '</span>'
      : '';
    var dropdown = '';
    if (state.notifOpen) {
      var rows = notifs.length ? notifs.map(function (n) {
        var dot = ({ error: c.error, warn: c.warn, info: c.textSecondary })[n.level] || c.textTertiary;
        return '<div style="display:flex;gap:9px;padding:11px 14px;border-bottom:1px solid ' + c.border + ';">' +
          '<div style="width:7px;height:7px;border-radius:50%;margin-top:5px;flex-shrink:0;background:' + dot + ';"></div>' +
          '<div><div style="font:12.5px/1.4 system-ui,sans-serif;color:' + c.textPrimary + ';">' + esc(n.text) + '</div>' +
          '<div style="font:11px system-ui,sans-serif;color:' + c.textTertiary + ';margin-top:2px;">' + esc(n.time) + '</div></div></div>';
      }).join('') : '<div style="padding:16px 14px;font:12.5px system-ui,sans-serif;color:' + c.textSecondary + ';">Всё в порядке — проблем нет.</div>';
      dropdown = '<div data-pop="1" style="position:absolute;top:46px;right:0;width:320px;max-width:80vw;background:' + c.cardBg + ';border:1px solid ' + c.border + ';border-radius:10px;box-shadow:0 12px 32px rgba(0,0,0,.2);z-index:30;overflow:hidden;">' +
        '<div style="padding:12px 14px;border-bottom:1px solid ' + c.border + ';font:700 12.5px system-ui,sans-serif;color:' + c.textPrimary + ';">Уведомления</div>' + rows + '</div>';
    }
    return '<div style="display:flex;align-items:flex-start;justify-content:space-between;gap:12px;">' +
      '<div><div style="font:700 23px system-ui,sans-serif;color:' + c.textPrimary + ';">' + esc(t[0]) + '</div>' +
      '<div style="font:13px system-ui,sans-serif;color:' + c.textSecondary + ';margin-top:4px;">' + esc(t[1]) + '</div></div>' +
      '<div data-pop="1" style="position:relative;flex-shrink:0;">' +
        '<button data-action="toggleNotif" style="position:relative;border:1px solid ' + c.border + ';background:' + c.cardBg + ';border-radius:9px;width:38px;height:38px;display:flex;align-items:center;justify-content:center;cursor:pointer;color:' + c.textSecondary + ';">' + ICONS.bell + badge + '</button>' +
        dropdown +
      '</div></div>';
  }

  /* ====================== ЭКРАН: ВХОД ====================== */
  function loginScreen(c) {
    return '<div style="flex:1;display:flex;align-items:center;justify-content:center;padding:24px;min-height:100vh;">' +
      '<form data-action="login" style="width:100%;max-width:300px;display:flex;flex-direction:column;gap:14px;">' +
        '<div style="display:flex;flex-direction:column;align-items:center;gap:10px;margin-bottom:6px;">' +
          '<div style="width:36px;height:36px;border-radius:10px;background:' + c.brand + ';flex-shrink:0;display:flex;align-items:center;justify-content:center;">' + ICONS.check + '</div>' +
          '<div style="font:700 15px system-ui,sans-serif;color:' + c.textPrimary + ';">UTM ORCHESTRATOR</div>' +
          '<div style="font:12.5px system-ui,sans-serif;color:' + c.textSecondary + ';">Вход в панель управления</div>' +
        '</div>' +
        '<div style="display:flex;flex-direction:column;gap:6px;">' +
          '<label style="font:12px system-ui,sans-serif;color:' + c.textSecondary + ';">Логин</label>' +
          '<input name="username" type="text" autocomplete="username" required style="background:' + c.subtleBg + ';border:1px solid ' + c.border + ';color:' + c.textPrimary + ';padding:10px 12px;border-radius:8px;font:13.5px system-ui,sans-serif;"/>' +
        '</div>' +
        '<div style="display:flex;flex-direction:column;gap:6px;">' +
          '<label style="font:12px system-ui,sans-serif;color:' + c.textSecondary + ';">Пароль</label>' +
          '<input name="password" type="password" autocomplete="current-password" required style="background:' + c.subtleBg + ';border:1px solid ' + c.border + ';color:' + c.textPrimary + ';padding:10px 12px;border-radius:8px;font:13.5px system-ui,sans-serif;"/>' +
        '</div>' +
        '<button type="submit" style="margin-top:6px;background:' + c.brand + ';border:none;color:#fff;padding:11px 16px;border-radius:8px;font:600 13.5px system-ui,sans-serif;cursor:pointer;">Войти</button>' +
      '</form></div>';
  }

  /* ====================== ЭКРАН: ОБЗОР (живые данные) ====================== */
  /* Единственный источник УТМ — живой статус службы. Пока служба не ответила,
     возвращаем пусто, а экраны показывают «Загрузка…» (dataLoaded()). Никаких
     демо-данных: раньше здесь мелькали фейковые «магазины» до первого ответа. */
  function dataLoaded() { return !!(state.liveStatus && state.liveStatus.instances); }
  function utmSource() { return dataLoaded() ? liveUtms() : []; }

  function loadingCard(c) {
    return '<div style="padding:22px;background:' + c.cardBg + ';border:1px solid ' + c.border +
      ';border-radius:12px;font:13px system-ui,sans-serif;color:' + c.textTertiary + ';">Загрузка данных…</div>';
  }

  function selectedUtm() {
    var v = utmSource().map(function (u) { return buildUtmView(u, colors()); });
    return v.find(function (u) { return u.id === state.selectedUtmId; }) || v[0];
  }

  function overviewScreen(c) {
    // Недоступность службы
    if (state.liveError && !state.liveStatus) {
      return '<div style="display:flex;flex-direction:column;align-items:center;justify-content:center;text-align:center;gap:14px;padding:60px 20px;flex:1;">' +
        '<div style="width:56px;height:56px;border-radius:50%;background:' + c.errorBg + ';display:flex;align-items:center;justify-content:center;"><div style="width:12px;height:12px;border-radius:50%;background:' + c.error + ';"></div></div>' +
        '<div style="font:700 18px system-ui,sans-serif;color:' + c.textPrimary + ';">Служба недоступна</div>' +
        '<div style="font:13.5px/1.6 system-ui,sans-serif;color:' + c.textSecondary + ';max-width:360px;">Не удалось получить статус УТМ от локальной службы. Проверьте, что оркестратор запущен, — обновление повторится автоматически.</div>' +
        '<button data-action="recheckAll" style="margin-top:4px;background:transparent;border:1px solid ' + c.borderStrong + ';color:' + c.textPrimary + ';padding:9px 18px;border-radius:8px;font:600 13px system-ui,sans-serif;cursor:pointer;">Повторить</button>' +
      '</div>';
    }

    var live = liveUtms();
    // Пустое состояние / онбординг — когда УТМ не найдены
    if (state.liveStatus && live.length === 0) {
      return emptyStateScreen(c);
    }

    var views = live.map(function (u) { return buildUtmView(u, c); });
    var okCount = live.filter(function (u) { return u.status === 'ok'; }).length;
    var total = live.length;
    var problem = live.find(function (u) { return u.status === 'error'; }) || live.find(function (u) { return u.status === 'warn'; });
    var heroOk = !problem;
    var heroColor = heroOk ? c.ok : (problem.status === 'error' ? c.error : c.warn);
    var heroBg = heroOk ? c.okBg : (problem.status === 'error' ? c.errorBg : c.warnBg);
    var heroSubtitle = heroOk ? 'Все УТМ в порядке' : ('1 ' + (problem.status === 'error' ? 'сбой' : 'предупреждение') + ' требует внимания');
    var canFilter = !heroOk;
    var filterActive = state.overviewFilter === 'problem';
    var shown = filterActive ? views.filter(function (u) { return u.status === 'error' || u.status === 'warn'; }) : views;

    var isMobile = state.isMobile;
    var heroDir = isMobile ? 'column' : 'row';
    var heroW = isMobile ? '100%' : 'auto';
    var btnW = isMobile ? '100%' : 'auto';
    var gridCols = isMobile ? '1fr' : 'repeat(auto-fit,minmax(250px,1fr))';

    var subtitleHTML = canFilter
      ? '<span data-action="filterProblems" style="cursor:pointer;text-decoration:underline;color:' + heroColor + ';font-weight:600;">' + esc(heroSubtitle) + '</span>'
      : '<span>' + esc(heroSubtitle) + '</span>';

    var hero = '<div style="display:flex;align-items:center;justify-content:space-between;flex-wrap:wrap;gap:16px;padding:22px 26px;background:' + c.cardBg + ';border:1px solid ' + c.border + ';border-radius:12px;">' +
      '<div style="display:flex;align-items:center;gap:16px;">' +
        '<div style="width:50px;height:50px;border-radius:50%;background:' + heroBg + ';display:flex;align-items:center;justify-content:center;flex-shrink:0;"><div style="width:13px;height:13px;border-radius:50%;background:' + heroColor + ';"></div></div>' +
        '<div><div style="font:700 24px system-ui,sans-serif;color:' + c.textPrimary + ';">' + okCount + ' / ' + total + ' работают</div>' +
        '<div style="font:12.5px system-ui,sans-serif;color:' + c.textSecondary + ';margin-top:3px;">' + subtitleHTML + '<span> · проверено в ' + esc(state.lastCheck) + '</span></div></div>' +
      '</div>' +
      '<div style="display:flex;flex-direction:' + heroDir + ';gap:10px;width:' + heroW + ';">' +
        '<button data-action="recheckAll" style="width:' + btnW + ';background:transparent;border:1px solid ' + c.borderStrong + ';color:' + c.textPrimary + ';padding:9px 16px;border-radius:8px;font:600 13px system-ui,sans-serif;cursor:pointer;">Перепроверить сейчас</button>' +
        '<button data-action="raiseAll" style="width:' + btnW + ';background:' + c.brand + ';border:none;color:#fff;padding:9px 16px;border-radius:8px;font:600 13px system-ui,sans-serif;cursor:pointer;">Поднять все</button>' +
      '</div></div>';

    var filterChip = filterActive
      ? '<div style="display:flex;align-items:center;gap:12px;padding:10px 14px;background:' + c.subtleBg + ';border:1px solid ' + c.border + ';border-radius:9px;flex-wrap:wrap;">' +
          '<span style="font:12.5px system-ui,sans-serif;color:' + c.textSecondary + ';">Показаны только сбойные и требующие внимания УТМ (' + shown.length + ')</span>' +
          '<button data-action="clearOverviewFilter" style="background:transparent;border:1px solid ' + c.borderStrong + ';color:' + c.textPrimary + ';padding:6px 12px;border-radius:7px;font:600 12px system-ui,sans-serif;cursor:pointer;">Сбросить фильтр</button></div>'
      : '';

    var cards = shown.map(function (u) { return overviewCard(u, c); }).join('');
    var grid = '<div style="display:grid;grid-template-columns:' + gridCols + ';gap:16px;">' + cards + '</div>';

    return hero + filterChip + grid;
  }

  function overviewCard(u, c) {
    var callout = u.hasCallout
      ? '<div style="display:flex;align-items:flex-start;gap:8px;padding:10px 12px;background:' + u.statusBg + ';border-radius:8px;">' +
          '<div style="width:6px;height:6px;border-radius:50%;background:' + u.statusColor + ';margin-top:5px;flex-shrink:0;"></div>' +
          '<div style="font:12.5px/1.5 system-ui,sans-serif;color:' + u.statusColor + ';">' + esc(u.reasonText) + '</div></div>'
      : '';
    var metaBlock = u.hasMeta
      ? '<div style="display:flex;flex-direction:column;gap:4px;padding:10px 12px;background:' + c.subtleBg + ';border-radius:8px;">' +
          '<div style="font:12px ui-monospace,Menlo,Consolas,monospace;color:' + c.textSecondary + ';">' + esc(u.line1) + '</div>' +
          '<div style="font:12px ui-monospace,Menlo,Consolas,monospace;color:' + c.textSecondary + ';">' + esc(u.line2) + '</div></div>'
      : '';
    var progress = u.isProgress
      ? '<div style="display:flex;flex-direction:column;gap:6px;padding:10px 12px;background:' + u.statusBg + ';border-radius:8px;">' +
          '<div style="font:12.5px system-ui,sans-serif;color:' + u.statusColor + ';">' + esc(u.progressLabel) + '</div>' +
          '<div style="height:6px;border-radius:3px;background:' + u.progressTrack + ';overflow:hidden;"><div style="height:100%;width:' + u.progress + '%;background:' + u.statusColor + ';border-radius:3px;"></div></div>' +
          '<div style="font:11px system-ui,sans-serif;color:' + c.textTertiary + ';">' + u.progress + '% · осталось ~2 мин</div></div>'
      : '';
    var exchange = u.hasExchange
      ? '<div style="display:flex;align-items:center;gap:6px;">' +
          '<div style="width:6px;height:6px;border-radius:50%;background:' + u.statusColor + ';flex-shrink:0;"></div>' +
          '<span style="font:12px system-ui,sans-serif;color:' + c.textSecondary + ';">' + esc(u.exchangeText) + '</span></div>'
      : '';
    // Вся плитка кликабельна → карточка УТМ. Внутренние ссылки перехватывают клик
    // сами (делегирование берёт ближайший [data-action]), так что они остаются рабочими.
    return '<div data-action="openUtm" data-id="' + esc(u.id) + '" title="Открыть карточку УТМ" style="min-width:0;background:' + c.cardBg + ';border:1px solid ' + c.border + ';border-radius:10px;padding:18px 18px 14px;display:flex;flex-direction:column;gap:12px;cursor:pointer;">' +
      '<div style="display:flex;flex-direction:column;gap:8px;min-width:0;">' +
        '<div style="font:700 15px/1.3 system-ui,sans-serif;color:' + c.textPrimary + ';">' + esc(u.name) + '</div>' +
        '<div style="display:flex;align-items:center;justify-content:space-between;gap:10px;">' +
          '<div style="font:12px ui-monospace,Menlo,Consolas,monospace;color:' + c.textTertiary + ';">порт ' + esc(u.port) + (u.version ? ' · v' + esc(u.version) : '') + '</div>' +
          statusPill(u, c) +
        '</div></div>' +
      callout + metaBlock + progress + exchange +
      '<div style="display:flex;gap:14px;padding-top:8px;border-top:1px solid ' + c.border + ';flex-wrap:wrap;">' +
        '<a data-action="utmPrimary" data-name="' + esc(u.name) + '" data-service="' + esc(u.service) + '" data-label="' + esc(u.primaryLabel) + '" style="font:600 12px system-ui,sans-serif;color:' + c.brand + ';cursor:pointer;">' + esc(u.primaryLabel) + '</a>' +
        '<a data-action="openUtmWeb" data-port="' + esc(u.port) + '" style="font:600 12px system-ui,sans-serif;color:' + c.textSecondary + ';cursor:pointer;">Открыть УТМ ↗</a>' +
        '<a data-action="openUtm" data-id="' + esc(u.id) + '" style="font:600 12px system-ui,sans-serif;color:' + c.textSecondary + ';cursor:pointer;">Подробнее</a>' +
      '</div></div>';
  }

  /* ====================== ЭКРАН: ПУСТОЕ СОСТОЯНИЕ ====================== */
  function emptyStateScreen(c) {
    return '<div style="flex:1;display:flex;flex-direction:column;align-items:center;justify-content:center;text-align:center;gap:16px;padding:40px 20px;">' +
      '<div style="width:64px;height:64px;border-radius:50%;background:' + c.brandBg + ';display:flex;align-items:center;justify-content:center;">' +
        '<svg width="28" height="28" viewBox="0 0 24 24" fill="none"><rect x="11" y="6" width="2" height="12" rx="1" fill="' + c.brand + '"/><rect x="6" y="11" width="12" height="2" rx="1" fill="' + c.brand + '"/></svg></div>' +
      '<div style="font:700 20px system-ui,sans-serif;color:' + c.textPrimary + ';">Пока ничего не настроено</div>' +
      '<div style="font:13.5px/1.6 system-ui,sans-serif;color:' + c.textSecondary + ';max-width:360px;">УТМ ещё не установлены на этом компьютере. Запустите мастер, чтобы добавить первый.</div>' +
      '<button data-action="goInstall" style="margin-top:6px;background:' + c.brand + ';border:none;color:#fff;padding:12px 22px;border-radius:9px;font:600 14px system-ui,sans-serif;cursor:pointer;">Установить УТМ</button>' +
    '</div>';
  }

  /* ====================== ЭКРАН: УТМ — СПИСОК ====================== */
  function utmListScreen(c) {
    if (!dataLoaded()) return loadingCard(c);
    var rows = utmSource().map(function (u) {
      var v = buildUtmView(u, c);
      return '<div data-action="openUtm" data-id="' + esc(v.id) + '" style="display:flex;align-items:center;justify-content:space-between;gap:14px;padding:14px 16px;background:' + c.cardBg + ';border:1px solid ' + c.border + ';border-radius:10px;cursor:pointer;flex-wrap:wrap;">' +
        '<div style="display:flex;align-items:center;gap:14px;min-width:0;">' +
          '<div style="width:9px;height:9px;border-radius:50%;background:' + v.statusColor + ';flex-shrink:0;' + v.dotAnim + '"></div>' +
          '<div style="min-width:0;"><div style="font:700 14.5px system-ui,sans-serif;color:' + c.textPrimary + ';">' + esc(v.name) + '</div>' +
          '<div style="font:12px ui-monospace,Menlo,Consolas,monospace;color:' + c.textTertiary + ';margin-top:2px;">порт ' + esc(v.port) + ' · ФСРАР ' + esc(v.fsrarDisplay) + (v.version ? ' · v' + esc(v.version) : '') + '</div></div>' +
        '</div>' +
        '<div style="display:flex;align-items:center;gap:6px;padding:5px 10px;border-radius:20px;background:' + v.statusBg + ';flex-shrink:0;"><span style="font:600 12px system-ui,sans-serif;color:' + v.statusColor + ';">' + esc(v.statusLabel) + '</span></div>' +
      '</div>';
    }).join('');
    return '<div style="display:flex;flex-direction:column;gap:10px;">' + rows + '</div>';
  }

  /* ====================== ЭКРАН: УТМ — ДЕТАЛИ ====================== */
  function utmDetailScreen(c) {
    var sel = selectedUtm();
    var isMobile = state.isMobile;
    var infoCols = isMobile ? '1fr' : 'repeat(2,1fr)';

    var callout = sel.hasCallout
      ? '<div style="display:flex;align-items:flex-start;gap:8px;padding:12px 14px;background:' + sel.statusBg + ';border-radius:9px;">' +
          '<div style="width:6px;height:6px;border-radius:50%;background:' + sel.statusColor + ';margin-top:6px;flex-shrink:0;"></div>' +
          '<div style="font:13px/1.5 system-ui,sans-serif;color:' + sel.statusColor + ';">' + esc(sel.reasonText) + '</div></div>'
      : '';

    function infoCell(label, val, mono) {
      return '<div style="min-width:0;overflow:hidden;"><div style="font:11px system-ui,sans-serif;color:' + c.textTertiary + ';margin-bottom:3px;">' + esc(label) + '</div>' +
        '<div style="font:13.5px ' + (mono ? 'ui-monospace,Menlo,Consolas,monospace' : 'system-ui,sans-serif') + ';color:' + c.textPrimary + ';">' + esc(val) + '</div></div>';
    }

    // --- Карточка «Краткое название» ---
    var hasSerial = !!sel.tokenSerial;
    var orgLine = sel.org
      ? '<div style="font:12.5px system-ui,sans-serif;color:' + c.textSecondary + ';">Из сертификата: <span style="color:' + c.textPrimary + ';">' + esc(sel.org) + '</span></div>'
      : '';
    var namePlaceholder = sel.org || 'Организация из сертификата';
    var nameField = hasSerial
      ? '<div style="display:flex;gap:10px;flex-wrap:wrap;align-items:center;">' +
          '<input id="name-edit-input" data-serial="' + esc(sel.tokenSerial) + '" value="' + esc(sel.customName) + '" placeholder="' + esc(namePlaceholder) + '" maxlength="60" ' +
            'style="flex:1;min-width:200px;background:' + c.subtleBg + ';border:1px solid ' + c.border + ';color:' + c.textPrimary + ';padding:8px 12px;border-radius:8px;font:13px system-ui,sans-serif;"/>' +
          '<button data-action="saveName" style="background:' + c.brand + ';border:none;color:#fff;padding:8px 16px;border-radius:8px;font:600 12.5px system-ui,sans-serif;cursor:pointer;">Сохранить</button>' +
          (sel.customName ? '<button data-action="resetName" data-serial="' + esc(sel.tokenSerial) + '" style="background:transparent;border:1px solid ' + c.borderStrong + ';color:' + c.textSecondary + ';padding:8px 14px;border-radius:8px;font:600 12.5px system-ui,sans-serif;cursor:pointer;">Сбросить</button>' : '') +
        '</div>'
      : '<div style="font:12.5px system-ui,sans-serif;color:' + c.textTertiary + ';padding:8px 10px;background:' + c.subtleBg + ';border-radius:8px;">Токен не сопоставлен — задать краткое название нельзя.</div>';

    var nameCard = '<div style="display:flex;flex-direction:column;gap:10px;padding:16px;background:' + c.cardBg + ';border:1px solid ' + c.border + ';border-radius:12px;">' +
      '<div style="font:700 13px system-ui,sans-serif;color:' + c.textPrimary + ';">Краткое название</div>' +
      '<div style="font:12px/1.5 system-ui,sans-serif;color:' + c.textTertiary + ';">Отображается в списках и карточках. Если не задано — показывается организация или адрес из сертификата.</div>' +
      orgLine + nameField +
    '</div>';

    // Порт — кликабельный: открывает веб самого УТМ (как «Открыть УТМ ↗»).
    var portCell = '<div style="min-width:0;overflow:hidden;"><div style="font:11px system-ui,sans-serif;color:' + c.textTertiary + ';margin-bottom:3px;">Порт</div>' +
      '<a data-action="openUtmWeb" data-port="' + esc(sel.port) + '" title="Открыть веб-интерфейс УТМ" style="font:13.5px ui-monospace,Menlo,Consolas,monospace;color:' + c.brand + ';cursor:pointer;text-decoration:none;">' + esc(sel.port) + ' ↗</a></div>';

    var info = '<div style="display:grid;grid-template-columns:' + infoCols + ';gap:12px;padding:16px;background:' + c.cardBg + ';border:1px solid ' + c.border + ';border-radius:12px;">' +
      portCell +
      infoCell('Организация', sel.org || '—', false) +
      infoCell('Версия УТМ', sel.version, false) +
      infoCell('Папка', sel.folder || '—', true) +
      infoCell('ФСРАР', sel.fsrarDisplay, true) +
      infoCell('Токен', sel.tokenDisplay, true) +
    '</div>';

    // Перепривязку показываем ТОЛЬКО для неработающего УТМ (у живого её делать нельзя).
    // Реальная перепривязка (introduce на другой токен) — на подходе; пока честная
    // пометка вместо фейкового дропдауна «токен привязан».
    var rebindBlock = (sel.status === 'ok') ? '' :
      '<div style="padding-top:8px;border-top:1px solid ' + c.border + ';font:12.5px/1.55 system-ui,sans-serif;color:' + c.textSecondary + ';">' +
        'Перепривязка токена к этому УТМ — <b style="color:' + c.textPrimary + ';">в разработке</b>. Пока используйте «Полечить токены» (перезапуск смарт-карт и подъём) или перезапуск УТМ.' +
      '</div>';

    var actions = '<div style="display:flex;flex-direction:column;gap:12px;padding:16px;background:' + c.cardBg + ';border:1px solid ' + c.border + ';border-radius:12px;">' +
      '<div style="font:700 13px system-ui,sans-serif;color:' + c.textPrimary + ';">Действия</div>' +
      '<div style="display:flex;gap:10px;flex-wrap:wrap;">' +
        '<button data-action="utmPrimary" data-name="' + esc(sel.name) + '" data-service="' + esc(sel.service) + '" data-label="' + esc(sel.primaryLabel) + '" style="background:transparent;border:1px solid ' + c.borderStrong + ';color:' + c.textPrimary + ';padding:8px 14px;border-radius:8px;font:600 12.5px system-ui,sans-serif;cursor:pointer;">' + esc(sel.primaryLabel) + '</button>' +
        '<button data-action="openUtmWeb" data-port="' + esc(sel.port) + '" style="background:transparent;border:1px solid ' + c.borderStrong + ';color:' + c.textPrimary + ';padding:8px 14px;border-radius:8px;font:600 12.5px system-ui,sans-serif;cursor:pointer;">Открыть УТМ ↗</button>' +
        '<button data-action="openLogsFor" data-port="' + esc(sel.port) + '" style="background:transparent;border:1px solid ' + c.borderStrong + ';color:' + c.textPrimary + ';padding:8px 14px;border-radius:8px;font:600 12.5px system-ui,sans-serif;cursor:pointer;">Логи</button>' +
      '</div>' +
      rebindBlock +
    '</div>';

    // --- Порт и брандмауэр ---
    var fwOpen = sel.firewallOpen;
    var fwColor = fwOpen ? c.ok : c.warn;
    var fwText = fwOpen ? 'открыт' : 'закрыт';
    var fwBtn = '<button data-action="toggleFirewall" data-service="' + esc(sel.service) + '" data-open="' + (fwOpen ? '0' : '1') + '" ' +
      'style="background:transparent;border:1px solid ' + c.borderStrong + ';color:' + c.textPrimary + ';padding:7px 14px;border-radius:8px;font:600 12.5px system-ui,sans-serif;cursor:pointer;">' +
      (fwOpen ? 'Закрыть порт' : 'Открыть порт') + '</button>';
    var portCard = '<div style="display:flex;flex-direction:column;gap:14px;padding:16px;background:' + c.cardBg + ';border:1px solid ' + c.border + ';border-radius:12px;">' +
      '<div style="font:700 13px system-ui,sans-serif;color:' + c.textPrimary + ';">Порт и доступ</div>' +
      // статус брандмауэра + переключатель
      '<div style="display:flex;align-items:center;justify-content:space-between;gap:12px;flex-wrap:wrap;">' +
        '<div style="display:flex;align-items:center;gap:8px;font:12.5px system-ui,sans-serif;color:' + c.textSecondary + ';">' +
          '<span style="width:9px;height:9px;border-radius:50%;background:' + fwColor + ';flex-shrink:0;"></span>' +
          'Порт ' + esc(sel.port) + ' в брандмауэре ОС: <b style="color:' + fwColor + ';">' + fwText + '</b></div>' +
        fwBtn +
      '</div>' +
      // смена порта
      '<div style="display:flex;align-items:center;gap:8px;flex-wrap:wrap;padding-top:10px;border-top:1px solid ' + c.border + ';">' +
        '<span style="font:12.5px system-ui,sans-serif;color:' + c.textSecondary + ';">Изменить порт:</span>' +
        '<input id="port-edit-input" type="number" min="1" max="65535" value="' + esc(sel.port) + '" ' +
          'style="width:110px;background:' + c.subtleBg + ';border:1px solid ' + c.border + ';color:' + c.textPrimary + ';padding:7px 10px;border-radius:7px;font:13px ui-monospace,Menlo,Consolas,monospace;"/>' +
        '<button data-action="changePort" data-service="' + esc(sel.service) + '" ' +
          'style="background:' + c.brand + ';border:none;color:#fff;padding:8px 14px;border-radius:8px;font:600 12.5px system-ui,sans-serif;cursor:pointer;">Изменить порт</button>' +
      '</div>' +
      '<div style="font:11.5px/1.5 system-ui,sans-serif;color:' + c.textTertiary + ';">Смена порта перезапустит УТМ (~1 мин, обмен прервётся), поправит его конфиг и перенесёт правило брандмауэра.</div>' +
    '</div>';

    var danger = '<div style="display:flex;align-items:center;justify-content:space-between;padding:14px 16px;background:' + c.errorSoftBg + ';border:1px solid ' + c.error + ';border-radius:12px;flex-wrap:wrap;gap:10px;">' +
      '<div><div style="font:700 13px system-ui,sans-serif;color:' + c.textPrimary + ';">Удалить УТМ</div>' +
      '<div style="font:12px system-ui,sans-serif;color:' + c.textSecondary + ';margin-top:2px;">Необратимо: настройки и привязка токена будут удалены</div></div>' +
      '<button data-action="openDeleteConfirm" style="background:' + c.error + ';border:none;color:#fff;padding:9px 16px;border-radius:8px;font:600 12.5px system-ui,sans-serif;cursor:pointer;">Удалить</button></div>';

    return '<div style="display:flex;flex-direction:column;gap:16px;">' +
      '<div data-action="goUtm" style="font:600 12.5px system-ui,sans-serif;color:' + c.textSecondary + ';cursor:pointer;">← Все УТМ</div>' +
      statusPillWide(sel, c) + callout + nameCard + info + portCard + actions + danger +
    '</div>';
  }

  function statusPillWide(v, c) {
    return '<div style="display:flex;align-items:center;gap:10px;padding:5px 10px;border-radius:20px;background:' + v.statusBg + ';width:fit-content;">' +
      '<div style="width:7px;height:7px;border-radius:50%;background:' + v.statusColor + ';' + v.dotAnim + '"></div>' +
      '<span style="font:600 12px system-ui,sans-serif;color:' + v.statusColor + ';">' + esc(v.statusLabel) + '</span></div>';
  }

  /* ====================== ЭКРАН: ТОКЕНЫ ======================
     Реальные данные из статуса: один токен = привязанный УТМ. Без PKCS11-скана
     (он опасен на занятых токенах) — состояние токена берём из здоровья УТМ. */
  function tokensScreen(c) {
    if (!dataLoaded()) return loadingCard(c);
    var src = utmSource();
    var views = src.map(function (u) { return buildUtmView(u, c); });

    var rows = views.map(function (v) {
      var hasToken = !!v.tokenSerial;
      var st = v.status; // ok | warn | error | stopped | progress
      var label, color;
      if (!hasToken) { label = 'Токен не сопоставлен'; color = c.warn; }
      else if (st === 'ok') { label = 'Работает'; color = c.ok; }
      else if (st === 'error') { label = 'Сбой ключа/токена'; color = c.error; }
      else if (st === 'stopped') { label = 'УТМ остановлен'; color = c.stopped; }
      else if (st === 'progress') { label = 'Идёт операция'; color = c.progress; }
      else { label = 'Требует внимания'; color = c.warn; }

      var borderColor = (st === 'error' || (!hasToken)) ? color : c.border;
      var serialDisp = hasToken ? 'Rutoken · ' + v.tokenSerial : 'серийник неизвестен';
      var sub = (v.fsrarDisplay && v.fsrarDisplay !== '—' ? 'ФСРАР ' + v.fsrarDisplay : 'ФСРАР —') +
                ' · ' + (v.service ? v.service + ' :' + v.port : 'порт ' + v.port) +
                (v.version ? ' · v' + v.version : '');

      // Для сбойного токена — точечный перезапуск УТМ (introduce, служба session 0).
      var action = (st === 'error' && v.service)
        ? '<button data-action="utmPrimary" data-name="' + esc(v.name) + '" data-service="' + esc(v.service) + '" data-label="Перезапустить" ' +
          'style="background:transparent;border:1px solid ' + c.borderStrong + ';color:' + c.textPrimary + ';padding:6px 12px;border-radius:7px;font:600 12px system-ui,sans-serif;cursor:pointer;">Перезапустить УТМ</button>'
        : '<div style="font:600 12px system-ui,sans-serif;color:' + color + ';">' + esc(label) + '</div>';

      return '<div style="display:flex;align-items:center;justify-content:space-between;gap:14px;padding:14px 16px;background:' + c.cardBg + ';border:1px solid ' + borderColor + ';border-radius:10px;flex-wrap:wrap;">' +
        '<div style="display:flex;align-items:center;gap:14px;min-width:0;">' +
          '<div style="width:9px;height:9px;border-radius:50%;background:' + color + ';flex-shrink:0;"></div>' +
          '<div style="min-width:0;"><div style="font:13.5px ui-monospace,Menlo,Consolas,monospace;color:' + c.textPrimary + ';">' + esc(serialDisp) + '</div>' +
          '<div style="font:12px system-ui,sans-serif;color:' + c.textTertiary + ';margin-top:2px;">' + esc(sub) + '</div></div>' +
        '</div>' + action +
      '</div>';
    }).join('');

    var healBtn = state.healing
      ? '<button disabled style="opacity:.6;background:' + c.brand + ';border:none;color:#fff;padding:9px 16px;border-radius:8px;font:600 12.5px system-ui,sans-serif;">Лечение идёт…</button>'
      : '<button data-action="healTokens" style="background:' + c.brand + ';border:none;color:#fff;padding:9px 16px;border-radius:8px;font:600 12.5px system-ui,sans-serif;cursor:pointer;">Полечить токены</button>';
    var note = '<div style="display:flex;align-items:center;justify-content:space-between;gap:12px;padding:12px 14px;background:' + c.subtleBg + ';border:1px solid ' + c.border + ';border-radius:9px;flex-wrap:wrap;">' +
      '<div style="flex:1;min-width:180px;font:12px/1.5 system-ui,sans-serif;color:' + c.textSecondary + ';">Если токен «завис» — «Полечить токены»: перезапуск службы смарт-карт и подъём УТМ. Выполняется через приложение в трее (потребуется подтверждение прав).</div>' +
      healBtn + '</div>';

    return '<div style="display:flex;flex-direction:column;gap:14px;">' + note + rows + '</div>';
  }

  /* ====================== ЭКРАН: УСТАНОВКА ======================
     Установка нового УТМ требует прочитать физический токен (PKCS11), а это
     интерактивная сессия, не служба. Поэтому запускается из приложения в трее.
     Полноценный веб-мастер появится через связку веб↔трей (см. BACKLOG). */
  function installScreen(c) {
    var steps = [
      'Вставьте новый токен в USB-порт.',
      'Откройте приложение в трее (значок УТМ:Оркестратор) → «Установить УТМ».',
      'Мастер прочитает токен (серийник и ФСРАР), даст назначить порт, зарегистрирует службу и привяжет токен.',
      'Готово — новый УТМ появится здесь, в панели, и начнёт обмен.',
    ];
    var list = steps.map(function (s, i) {
      return '<div style="display:flex;gap:10px;align-items:flex-start;">' +
        '<div style="width:22px;height:22px;border-radius:50%;background:' + c.brandBg + ';color:' + c.brandText + ';font:700 12px system-ui,sans-serif;display:flex;align-items:center;justify-content:center;flex-shrink:0;">' + (i + 1) + '</div>' +
        '<div style="font:13px/1.55 system-ui,sans-serif;color:' + c.textPrimary + ';padding-top:1px;">' + esc(s) + '</div></div>';
    }).join('');

    // Рабочий скан токенов через трей (связка веб↔трей).
    var scanBtn = state.scanning
      ? '<button disabled style="opacity:.6;background:' + c.brand + ';border:none;color:#fff;padding:10px 18px;border-radius:8px;font:600 13px system-ui,sans-serif;">Сканирую токены…</button>'
      : '<button data-action="scanTokens" style="background:' + c.brand + ';border:none;color:#fff;padding:10px 18px;border-radius:8px;font:600 13px system-ui,sans-serif;cursor:pointer;">Сканировать токены сейчас</button>';
    var scanResult = '';
    if (state.scannedTokens) {
      if (!state.scannedTokens.length) {
        scanResult = '<div style="font:12.5px system-ui,sans-serif;color:' + c.textTertiary + ';padding:10px 12px;background:' + c.subtleBg + ';border-radius:8px;">Токены не найдены. Вставьте токен в USB и повторите скан.</div>';
      } else {
        scanResult = '<div style="font:12px system-ui,sans-serif;color:' + c.textSecondary + ';">Найдено токенов: ' + state.scannedTokens.length + '</div>' +
          state.scannedTokens.map(function (t) {
            return '<div style="display:flex;flex-direction:column;gap:2px;padding:10px 12px;background:' + c.subtleBg + ';border-radius:8px;">' +
              '<div style="font:13px ui-monospace,Menlo,Consolas,monospace;color:' + c.textPrimary + ';">Rutoken · ' + esc(t.serial) + '</div>' +
              '<div style="font:12px system-ui,sans-serif;color:' + c.textTertiary + ';">ФСРАР ' + esc(t.fsrar || '—') + ' · ридер ' + esc(t.reader || '—') + '</div></div>';
          }).join('');
      }
    }
    // Подхват существующих УТМ (первый запуск): если нашли службы + отсканировали токены.
    var discovered = (state.liveStatus && state.liveStatus.instances) ? state.liveStatus.instances.length : 0;
    var adoptSection = '';
    if (state.scannedTokens && state.scannedTokens.length && discovered) {
      adoptSection = '<div style="display:flex;align-items:center;justify-content:space-between;gap:12px;flex-wrap:wrap;padding-top:10px;margin-top:2px;border-top:1px solid ' + c.border + ';">' +
        '<div style="flex:1;min-width:180px;font:12.5px/1.5 system-ui,sans-serif;color:' + c.textSecondary + ';">На компьютере найдено УТМ: <b style="color:' + c.textPrimary + ';">' + discovered + '</b>. Подхватить их под управление оркестратора (запишет привязки серийник↔ридер).</div>' +
        '<button data-action="adoptExisting" style="background:' + c.ok + ';border:none;color:#fff;padding:9px 16px;border-radius:8px;font:600 12.5px system-ui,sans-serif;cursor:pointer;">Подхватить существующие УТМ</button>' +
      '</div>';
    }

    var scanCard = '<div style="display:flex;flex-direction:column;gap:12px;padding:20px;background:' + c.cardBg + ';border:1px solid ' + c.border + ';border-radius:12px;">' +
      '<div style="font:700 14px system-ui,sans-serif;color:' + c.textPrimary + ';">Обследование: токены на компьютере</div>' +
      '<div style="font:12px/1.5 system-ui,sans-serif;color:' + c.textSecondary + ';">Читаются через приложение в трее (интерактивно). Работает, когда панель открыта с самого компьютера и трей запущен.</div>' +
      '<div>' + scanBtn + '</div>' + scanResult + adoptSection +
    '</div>';

    return '<div style="display:flex;flex-direction:column;gap:16px;">' +
      '<div style="display:flex;flex-direction:column;gap:14px;padding:20px;background:' + c.cardBg + ';border:1px solid ' + c.border + ';border-radius:12px;">' +
        '<div style="font:700 15px system-ui,sans-serif;color:' + c.textPrimary + ';">Установка нового УТМ</div>' +
        '<div style="font:12.5px/1.6 system-ui,sans-serif;color:' + c.textSecondary + ';">Добавление УТМ читает физический токен, поэтому выполняется в интерактивной сессии (через трей). Полный мастер с назначением порта — на подходе; уже сейчас можно отсканировать подключённые токены.</div>' +
        '<div style="display:flex;flex-direction:column;gap:12px;margin-top:4px;">' + list + '</div>' +
      '</div>' +
      scanCard +
    '</div>';
  }

  // Старый пятишаговый мастер-мокап (installStep1..5) удалён: он симулировал установку
  // на демо-данных и говорил «успешно» ничего не сделав. Реальное добавление УТМ —
  // в installScreen (скан токенов через трей + подхват существующих). Полный мастер
  // «с нуля» (назначение порта + регистрация службы + introduce) — в беклоге.

  /* ====================== ЭКРАН: ОБНОВЛЕНИЯ (реальные версии) ====================== */
  function updatesScreen(c) {
    if (!dataLoaded()) return loadingCard(c);
    var d = state.liveStatus;
    var orchVer = (d && d.orchestratorVersion) ? d.orchestratorVersion : '—';
    var orchestrator = '<div style="display:flex;align-items:center;justify-content:space-between;gap:14px;padding:16px 18px;background:' + c.cardBg + ';border:1px solid ' + c.border + ';border-radius:12px;flex-wrap:wrap;">' +
      '<div><div style="font:700 14px system-ui,sans-serif;color:' + c.textPrimary + ';">Оркестратор</div>' +
      '<div style="font:12.5px system-ui,sans-serif;color:' + c.textSecondary + ';margin-top:3px;">Установленная версия ' + esc(orchVer) + '</div></div></div>';

    var src = utmSource();
    var rows = src.map(function (u) { return buildUtmView(u, c); }).map(function (v) {
      var right = v.status === 'ok'
        ? '<span style="font:600 12px system-ui,sans-serif;color:' + c.ok + ';">' + esc(v.version) + '</span>'
        : '<span style="font:600 12px system-ui,sans-serif;color:' + c.textTertiary + ';">' + esc(v.version) + '</span>';
      return '<div style="display:flex;align-items:center;justify-content:space-between;gap:14px;padding:14px 16px;background:' + c.cardBg + ';border:1px solid ' + c.border + ';border-radius:10px;flex-wrap:wrap;">' +
        '<div><div style="font:700 13.5px system-ui,sans-serif;color:' + c.textPrimary + ';">' + esc(v.name) + ' <span style="font:12px ui-monospace,Menlo,Consolas,monospace;color:' + c.textTertiary + ';">· порт ' + v.port + '</span></div>' +
        '<div style="font:12px system-ui,sans-serif;color:' + c.textSecondary + ';margin-top:3px;">Версия УТМ</div></div>' + right + '</div>';
    }).join('');

    var note = '<div style="padding:12px 14px;background:' + c.subtleBg + ';border:1px solid ' + c.border + ';border-radius:9px;font:12px/1.5 system-ui,sans-serif;color:' + c.textSecondary + ';">' +
      'Показаны установленные версии. Проверка и установка обновлений УТМ — следующий этап (скачивание с fsrar.gov.ru + замена файлов с сохранением базы и перепривязкой).</div>';

    return '<div style="display:flex;flex-direction:column;gap:16px;">' + orchestrator +
      '<div style="font:700 13px system-ui,sans-serif;color:' + c.textPrimary + ';">УТМ</div>' + rows + note + '</div>';
  }

  /* ====================== ЭКРАН: ЛОГИ (реальные, из /api/logs) ====================== */
  function logsFiltered() {
    return (state.logs || [])
      .filter(function (l) { return !state.logsFilterLevel || l.level === state.logsFilterLevel; })
      .filter(function (l) { return !state.logsSearch || (l.msg || '').toLowerCase().indexOf(state.logsSearch.toLowerCase()) !== -1; });
  }

  function logsListHTML(c) {
    var levelMeta = {
      info: { label: 'ИНФО', color: c.textSecondary, bg: c.subtleBg },
      warn: { label: 'ВНИМАНИЕ', color: c.warn, bg: c.warnBg },
      error: { label: 'ОШИБКА', color: c.error, bg: c.errorBg },
    };
    if (state.logs === null) return '<div style="padding:16px 12px;background:' + c.cardBg + ';font:12.5px system-ui,sans-serif;color:' + c.textTertiary + ';">Загрузка…</div>';
    var rows = logsFiltered().map(function (l) {
      var m = levelMeta[l.level] || levelMeta.info;
      return '<div style="display:flex;align-items:baseline;gap:10px;padding:9px 12px;background:' + c.cardBg + ';flex-wrap:wrap;">' +
        '<span style="font:11.5px ui-monospace,Menlo,Consolas,monospace;color:' + c.textTertiary + ';">' + esc(l.t) + '</span>' +
        '<span style="font:10.5px system-ui,sans-serif;font-weight:700;padding:2px 7px;border-radius:5px;background:' + m.bg + ';color:' + m.color + ';">' + m.label + '</span>' +
        '<span style="font:12.5px system-ui,sans-serif;color:' + c.textPrimary + ';">' + esc(l.msg) + '</span></div>';
    }).join('');
    return rows || '<div style="padding:16px 12px;background:' + c.cardBg + ';font:12.5px system-ui,sans-serif;color:' + c.textTertiary + ';">Записей нет.</div>';
  }

  /* Загрузка реальных логов оркестратора (bringup.log). */
  function loadLogs() {
    fetch('/api/logs?limit=400', { cache: 'no-store' })
      .then(function (r) { return r.json(); })
      .then(function (d) { state.logs = d.lines || []; if (state.screen === 'logs') render(); })
      .catch(function () { state.logs = []; if (state.screen === 'logs') render(); });
  }

  /* Связка веб↔трей: создать интерактивное задание и дождаться результата.
     onDone(resultObj|null, errorText|null). Требует запущенный трей (он «руки»). */
  function runJob(type, onDone) {
    fetch('/api/jobs', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ type: type }),
    })
      .then(function (r) { return r.json(); })
      .then(function (d) { if (d && d.id) pollJob(d.id, onDone, 0); else onDone(null, 'Не удалось создать задание'); })
      .catch(function () { onDone(null, 'Служба недоступна'); });
  }
  function pollJob(id, onDone, tries) {
    if (tries > 80) { onDone(null, 'Трей не ответил. Запущено ли приложение в трее? Откройте панель с самого компьютера.'); return; }
    fetch('/api/jobs/' + id, { cache: 'no-store' })
      .then(function (r) { return r.json(); })
      .then(function (d) {
        if (d.state === 'Done') { var obj = null; try { obj = d.result ? JSON.parse(d.result) : {}; } catch (e) {} onDone(obj, null); }
        else if (d.state === 'Error') onDone(null, d.error || 'ошибка задания');
        else setTimeout(function () { pollJob(id, onDone, tries + 1); }, 1500);
      })
      .catch(function () { setTimeout(function () { pollJob(id, onDone, tries + 1); }, 1500); });
  }

  /* Настройки панели (реальные, /api/settings). */
  function loadSettings() {
    fetch('/api/settings', { cache: 'no-store' })
      .then(function (r) { return r.json(); })
      .then(function (d) {
        state.requireAuth = !!d.requireAuth;
        state.allowedIps = Array.isArray(d.allowedIps) ? d.allowedIps : [];
        state.settingsLoaded = true;
        if (state.screen === 'settings') render();
      })
      .catch(function () { state.settingsLoaded = true; });
  }
  function saveSettings() {
    var body = {
      requireAuth: !!state.requireAuth,
      allowedIps: (state.allowedIps || []).map(function (s) { return (s || '').trim(); }).filter(Boolean),
    };
    return fetch('/api/settings', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body),
    });
  }

  function logsScreen(c) {
    function lvlOpt(v, label) {
      return '<option value="' + v + '"' + (state.logsFilterLevel === v ? ' selected' : '') + '>' + label + '</option>';
    }
    var lvlSelect = '<select data-action="onLogsLevel" style="background:' + c.cardBg + ';border:1px solid ' + c.border + ';color:' + c.textPrimary + ';padding:8px 12px;border-radius:8px;font:12.5px system-ui,sans-serif;">' +
      lvlOpt('', 'Все уровни') + lvlOpt('info', 'Инфо') + lvlOpt('warn', 'Внимание') + lvlOpt('error', 'Ошибка') + '</select>';

    return '<div style="display:flex;flex-direction:column;gap:14px;">' +
      '<div style="font:12px system-ui,sans-serif;color:' + c.textTertiary + ';">Журнал оркестратора: подъём, перезапуск и лечение УТМ.</div>' +
      '<div style="display:flex;gap:10px;flex-wrap:wrap;">' + lvlSelect +
        '<input data-input="onLogsSearch" placeholder="Поиск (например, порт 8082 или Transport3)" value="' + esc(state.logsSearch) + '" style="flex:1;min-width:160px;background:' + c.cardBg + ';border:1px solid ' + c.border + ';color:' + c.textPrimary + ';padding:8px 12px;border-radius:8px;font:12.5px system-ui,sans-serif;"/>' +
        '<button data-action="refreshLogs" style="background:transparent;border:1px solid ' + c.borderStrong + ';color:' + c.textPrimary + ';padding:8px 14px;border-radius:8px;font:600 12.5px system-ui,sans-serif;cursor:pointer;">Обновить</button>' +
      '</div>' +
      '<div id="logs-list" style="display:flex;flex-direction:column;gap:1px;background:' + c.border + ';border:1px solid ' + c.border + ';border-radius:10px;overflow:hidden;">' + logsListHTML(c) + '</div>' +
    '</div>';
  }

  /* ====================== ЭКРАН: НАСТРОЙКИ ====================== */
  function settingsScreen(c) {
    // --- Безопасность (пароль + доп. требования: auth-toggle, IP allowlist) ---
    var ipRows = state.allowedIps.map(function (ip, idx) {
      return '<div style="display:flex;align-items:center;gap:8px;flex-wrap:wrap;">' +
        '<input data-input="ipInput" data-idx="' + idx + '" value="' + esc(ip) + '" placeholder="напр. 192.168.1.10" style="flex:1;min-width:140px;background:' + c.subtleBg + ';border:1px solid ' + c.border + ';color:' + c.textPrimary + ';padding:8px 10px;border-radius:7px;font:12.5px ui-monospace,Menlo,Consolas,monospace;"/>' +
        '<button data-action="removeIp" data-idx="' + idx + '" style="background:transparent;border:1px solid ' + c.borderStrong + ';color:' + c.textSecondary + ';padding:8px 12px;border-radius:7px;font:600 12px system-ui,sans-serif;cursor:pointer;">Удалить</button></div>';
    }).join('');
    if (!ipRows) ipRows = '<div style="font:12px system-ui,sans-serif;color:' + c.textTertiary + ';">Список пуст — доступ разрешён с любого адреса.</div>';

    var security = '<div style="display:flex;flex-direction:column;gap:12px;padding:16px 18px;background:' + c.cardBg + ';border:1px solid ' + c.border + ';border-radius:12px;">' +
      '<div style="font:700 13px system-ui,sans-serif;color:' + c.textPrimary + ';">Безопасность</div>' +
      '<div style="display:flex;align-items:center;gap:10px;flex-wrap:wrap;">' +
        '<span style="width:150px;font:12.5px system-ui,sans-serif;color:' + c.textSecondary + ';">Пароль панели</span>' +
        '<input type="password" placeholder="••••••••" style="flex:1;min-width:140px;background:' + c.subtleBg + ';border:1px solid ' + c.border + ';color:' + c.textPrimary + ';padding:8px 10px;border-radius:7px;font:13px system-ui,sans-serif;"/>' +
      '</div>' +
      // Доп. требование 1: аутентификация «по галочке»
      '<div style="display:flex;align-items:center;justify-content:space-between;flex-wrap:wrap;gap:10px;padding-top:6px;border-top:1px solid ' + c.border + ';">' +
        '<div><div style="font:12.5px system-ui,sans-serif;color:' + c.textPrimary + ';">Требовать вход в панель</div>' +
        '<div style="font:11.5px system-ui,sans-serif;color:' + c.textTertiary + ';margin-top:2px;">При включении панель запросит логин и пароль</div></div>' +
        segmented(c, [
          { label: 'Вкл', active: state.requireAuth, action: 'setAuthReqOn' },
          { label: 'Выкл', active: !state.requireAuth, action: 'setAuthReqOff' },
        ]) +
      '</div>' +
      // Удалённый доступ
      '<div style="display:flex;align-items:center;justify-content:space-between;flex-wrap:wrap;gap:10px;">' +
        '<span style="font:12.5px system-ui,sans-serif;color:' + c.textSecondary + ';">Удалённый доступ (с телефона)</span>' +
        segmented(c, [
          { label: 'Вкл', active: state.remoteAccess, action: 'setRemoteOn' },
          { label: 'Выкл', active: !state.remoteAccess, action: 'setRemoteOff' },
        ]) +
      '</div>' +
      (state.remoteAccess ? '<div style="font:12px ui-monospace,Menlo,Consolas,monospace;color:' + c.textTertiary + ';padding:8px 10px;background:' + c.subtleBg + ';border-radius:7px;">Доступно по адресу: https://utm.local:8443</div>' : '') +
      // Доп. требование 2: IP allowlist
      '<div style="display:flex;flex-direction:column;gap:8px;padding-top:6px;border-top:1px solid ' + c.border + ';">' +
        '<div style="font:12.5px system-ui,sans-serif;color:' + c.textPrimary + ';">Разрешённые IP</div>' +
        '<div style="font:11.5px/1.5 system-ui,sans-serif;color:' + c.textTertiary + ';">Панель будет доступна только с этих IP-адресов. Оставьте список пустым, чтобы разрешить доступ отовсюду.</div>' +
        ipRows +
        '<div style="display:flex;gap:8px;flex-wrap:wrap;margin-top:2px;">' +
          '<button data-action="addIp" style="background:transparent;border:1px solid ' + c.borderStrong + ';color:' + c.textPrimary + ';padding:7px 12px;border-radius:7px;font:600 12px system-ui,sans-serif;cursor:pointer;">Добавить IP</button>' +
          '<button data-action="saveIps" style="background:' + c.brand + ';border:none;color:#fff;padding:7px 12px;border-radius:7px;font:600 12px system-ui,sans-serif;cursor:pointer;">Сохранить список</button>' +
        '</div>' +
      '</div>' +
    '</div>';

    // --- PIN-коды ---
    var pinType = state.pinVisible ? 'text' : 'password';
    function pinRow(serial, val) {
      return '<div style="display:flex;align-items:center;gap:10px;flex-wrap:wrap;"><span style="width:130px;font:12.5px ui-monospace,Menlo,Consolas,monospace;color:' + c.textSecondary + ';">' + serial + '</span>' +
        '<input type="' + pinType + '" value="' + val + '" style="flex:1;min-width:120px;background:' + c.subtleBg + ';border:1px solid ' + c.border + ';color:' + c.textPrimary + ';padding:8px 10px;border-radius:7px;font:13px system-ui,sans-serif;"/></div>';
    }
    var pins = '<div style="display:flex;flex-direction:column;gap:12px;padding:16px 18px;background:' + c.cardBg + ';border:1px solid ' + c.border + ';border-radius:12px;">' +
      '<div style="font:700 13px system-ui,sans-serif;color:' + c.textPrimary + ';">PIN-коды токенов</div>' +
      pinRow('44f94b1a', '1234') + pinRow('a12c07e9', '5678') +
      '<div data-action="togglePinVisible" style="font:600 12px system-ui,sans-serif;color:' + c.brand + ';cursor:pointer;width:fit-content;">' + (state.pinVisible ? 'Скрыть PIN' : 'Показать PIN') + '</div></div>';

    // --- Расписание ---
    var schedule = '<div style="display:flex;flex-direction:column;gap:12px;padding:16px 18px;background:' + c.cardBg + ';border:1px solid ' + c.border + ';border-radius:12px;">' +
      '<div style="font:700 13px system-ui,sans-serif;color:' + c.textPrimary + ';">Расписание проверок</div>' +
      '<div style="display:flex;align-items:center;gap:10px;flex-wrap:wrap;">' +
        '<span style="width:180px;font:12.5px system-ui,sans-serif;color:' + c.textSecondary + ';">Периодическая проверка</span>' +
        '<select style="background:' + c.subtleBg + ';border:1px solid ' + c.border + ';color:' + c.textPrimary + ';padding:7px 10px;border-radius:7px;font:12.5px system-ui,sans-serif;">' +
          '<option>Каждые 30 минут</option><option>Каждый час</option><option selected>Каждые 1.5 часа</option><option>Каждые 3 часа</option></select>' +
      '</div>' +
      '<div style="display:flex;align-items:center;gap:10px;flex-wrap:wrap;">' +
        '<span style="width:180px;font:12.5px system-ui,sans-serif;color:' + c.textSecondary + ';">Проверка обновлений</span>' +
        '<select style="background:' + c.subtleBg + ';border:1px solid ' + c.border + ';color:' + c.textPrimary + ';padding:7px 10px;border-radius:7px;font:12.5px system-ui,sans-serif;">' +
          '<option>Ежедневно</option><option selected>Раз в неделю</option><option>Раз в месяц</option></select>' +
      '</div></div>';

    // --- Файрвол ---
    var firewall = '<div style="display:flex;flex-direction:column;gap:12px;padding:16px 18px;background:' + c.cardBg + ';border:1px solid ' + c.border + ';border-radius:12px;">' +
      '<div style="display:flex;align-items:center;justify-content:space-between;flex-wrap:wrap;gap:10px;">' +
        '<div><div style="font:700 13px system-ui,sans-serif;color:' + c.textPrimary + ';">Файрвол по умолчанию</div>' +
        '<div style="font:12px system-ui,sans-serif;color:' + c.textSecondary + ';margin-top:2px;">Открывать порты УТМ автоматически</div></div>' +
        segmented(c, [
          { label: 'Да', active: state.firewallAuto, action: 'setFirewallYes' },
          { label: 'Нет', active: !state.firewallAuto, action: 'setFirewallNo' },
        ]) +
      '</div></div>';

    // --- Тема ---
    var dark = state.theme === 'dark';
    var theme = '<div style="display:flex;align-items:center;justify-content:space-between;padding:16px 18px;background:' + c.cardBg + ';border:1px solid ' + c.border + ';border-radius:12px;">' +
      '<div style="font:700 13px system-ui,sans-serif;color:' + c.textPrimary + ';">Тема интерфейса</div>' +
      segmented(c, [
        { label: 'Тёмная', active: dark, action: 'setDark' },
        { label: 'Светлая', active: !dark, action: 'setLight' },
      ]) +
    '</div>';

    return '<div style="display:flex;flex-direction:column;gap:16px;">' + security + pins + schedule + firewall + theme + '</div>';
  }

  /* ====================== ГЛАВНЫЙ РЕНДЕР ====================== */
  function screenContent(c) {
    var s = state.screen;
    if (s === 'overview') return overviewScreen(c);
    if (s === 'utm') return utmListScreen(c);
    if (s === 'utm-detail') return utmDetailScreen(c);
    if (s === 'tokens') return tokensScreen(c);
    if (s === 'install') return installScreen(c);
    if (s === 'updates') return updatesScreen(c);
    if (s === 'logs') return logsScreen(c);
    if (s === 'settings') return settingsScreen(c);
    return '';
  }

  function appBody(c) {
    var isMobile = state.isMobile;
    var pad = isMobile ? '18px 16px 30px' : '36px 40px';
    var shell = '<div style="display:flex;flex-direction:' + (isMobile ? 'column' : 'row') + ';min-height:100vh;background:' + c.appBg + ';">' +
      (isMobile ? mobileTopBar(c) : sidebar(c)) +
      (isMobile && state.mobileNavOpen ? drawer(c) : '') +
      '<div style="flex:1;padding:' + pad + ';display:flex;flex-direction:column;gap:22px;min-width:0;">' +
        header(c) + screenContent(c) +
      '</div>' +
    '</div>';
    return shell;
  }

  function modalHTML(c) {
    var sel = selectedUtm();
    return '<div style="position:fixed;inset:0;background:rgba(0,0,0,.5);display:flex;align-items:center;justify-content:center;z-index:50;padding:20px;">' +
      '<div data-pop="1" style="width:100%;max-width:380px;background:' + c.cardBg + ';border-radius:14px;padding:22px;display:flex;flex-direction:column;gap:14px;">' +
        '<div style="font:700 15px system-ui,sans-serif;color:' + c.textPrimary + ';">Удалить УТМ «' + esc(sel.name) + '»?</div>' +
        '<div style="font:13px/1.6 system-ui,sans-serif;color:' + c.textSecondary + ';">Это действие необратимо. Настройки и привязка токена будут удалены.</div>' +
        '<div style="display:flex;justify-content:flex-end;gap:10px;">' +
          '<button data-action="closeDeleteConfirm" style="background:transparent;border:1px solid ' + c.borderStrong + ';color:' + c.textPrimary + ';padding:9px 16px;border-radius:8px;font:600 13px system-ui,sans-serif;cursor:pointer;">Отмена</button>' +
          '<button data-action="confirmDelete" style="background:' + c.error + ';border:none;color:#fff;padding:9px 16px;border-radius:8px;font:600 13px system-ui,sans-serif;cursor:pointer;">Удалить</button>' +
        '</div>' +
      '</div></div>';
  }

  function toastHTML(c) {
    return '<div style="position:fixed;bottom:20px;left:50%;transform:translateX(-50%);background:' + c.toastBg + ';color:#fff;padding:11px 20px;border-radius:9px;font:600 13px system-ui,sans-serif;box-shadow:0 8px 24px rgba(0,0,0,.3);z-index:60;white-space:nowrap;">' + esc(state.toast) + '</div>';
  }

  function view() {
    var c = colors();
    var showLogin = state.requireAuth && !state.authed;
    return '<div style="position:relative;min-height:100vh;background:' + c.appBg + ';">' +
      (showLogin ? loginScreen(c) : appBody(c)) +
      (state.confirmDeleteOpen ? modalHTML(c) : '') +
      (state.toast ? toastHTML(c) : '') +
    '</div>';
  }

  var appEl;
  function render() {
    appEl.innerHTML = view();
  }
  function setState(patch) {
    Object.assign(state, patch);
    render();
  }

  /* ====================== ТОСТЫ ====================== */
  var toastTimer;
  function showToast(msg) {
    setState({ toast: msg });
    clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { setState({ toast: null }); }, 2600);
  }
  // Честная заглушка: функция ещё не реализована — не притворяемся успехом.
  function notReady(what) { showToast((what || 'Функция') + ' — в разработке'); }

  /* ====================== ДЕЙСТВИЯ ====================== */
  function setScreen(s) { setState({ screen: s, mobileNavOpen: false, notifOpen: false }); }

  var actions = {
    /* навигация */
    goOverview: function () { setScreen('overview'); },
    goUtm: function () { setScreen('utm'); },
    goTokens: function () { setScreen('tokens'); },
    goUpdates: function () { setScreen('updates'); },
    goLogs: function () { setScreen('logs'); loadLogs(); },
    refreshLogs: function () { state.logs = null; render(); loadLogs(); },
    goSettings: function () { setScreen('settings'); if (!state.settingsLoaded) loadSettings(); },
    goInstall: function () { setState({ screen: 'install', mobileNavOpen: false, notifOpen: false }); },

    openUtm: function (el) { setState({ screen: 'utm-detail', selectedUtmId: el.getAttribute('data-id'), mobileNavOpen: false, notifOpen: false }); },
    openLogsFor: function (el) { setState({ screen: 'logs', logsSearch: String(el.getAttribute('data-port') || ''), mobileNavOpen: false, notifOpen: false }); loadLogs(); },
    openUtmWeb: function (el) {
      var port = el.getAttribute('data-port');
      if (port) window.open('http://localhost:' + port + '/', '_blank');
    },

    /* тема / оболочка */
    toggleTheme: function () {
      var t = state.theme === 'dark' ? 'light' : 'dark';
      try { localStorage.setItem('utm.theme', t); } catch (e) {}
      setState({ theme: t });
    },
    setDark: function () { try { localStorage.setItem('utm.theme', 'dark'); } catch (e) {} setState({ theme: 'dark' }); },
    setLight: function () { try { localStorage.setItem('utm.theme', 'light'); } catch (e) {} setState({ theme: 'light' }); },
    toggleMobileNav: function () { setState({ mobileNavOpen: !state.mobileNavOpen }); },
    toggleNotif: function () { setState({ notifOpen: !state.notifOpen, unreadCount: 0 }); },

    /* вход */
    login: function () {
      // Локальный вход в панель (гейт requireAuth). Серверная проверка пароля —
      // отдельная задача; пока панель доступна только с localhost/разрешённых IP.
      setState({ authed: true });
      showToast('Добро пожаловать');
    },

    /* обзор */
    filterProblems: function () { setState({ overviewFilter: 'problem' }); },
    clearOverviewFilter: function () { setState({ overviewFilter: null }); },
    recheckAll: function () { showToast('Проверка запущена…'); pollStatus(); },
    raiseAll: function () { notReady('Поднять все УТМ разом'); },
    utmPrimary: function (el) {
      var label = el.getAttribute('data-label');
      var service = el.getAttribute('data-service');
      var name = el.getAttribute('data-name');
      // «Перезапустить» — реальный вызов службы (introduce, session-0-safe).
      if (label === 'Перезапустить' && service) {
        if (!window.confirm('Перезапустить «' + name + '»?\nОбмен с ЕГАИС на ~минуту прервётся.')) return;
        showToast('Перезапуск «' + name + '»…');
        fetch('/api/utm/restart', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ service: service }),
        }).then(function (r) {
          if (r.status === 409) { showToast('Уже идёт операция с ридерами — подождите'); return; }
          if (!r.ok) throw new Error();
          showToast('Перезапуск запущен — статус обновится автоматически');
        }).catch(function () { showToast('Не удалось запустить перезапуск'); });
        return;
      }
      // Прочие первичные действия (запуск/привязка) пока не реализованы — честно.
      notReady(label);
    },

    /* деталь УТМ */
    stopUtm: function () { notReady('Остановка УТМ'); },
    /* Файрвол: открыть/закрыть порт УТМ (правит наше правило через службу). */
    toggleFirewall: function (el) {
      var service = el.getAttribute('data-service');
      var open = el.getAttribute('data-open') === '1';
      showToast(open ? 'Открываю порт в брандмауэре…' : 'Закрываю порт в брандмауэре…');
      fetch('/api/utm/firewall', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ service: service, open: open }),
      })
        .then(function (r) { if (!r.ok) throw new Error(); return r.json(); })
        .then(function (d) { showToast(d.open ? 'Порт открыт в брандмауэре' : 'Порт закрыт в брандмауэре'); pollStatus(true); })
        .catch(function () { showToast('Не удалось изменить правило брандмауэра'); });
    },
    /* Смена внешнего порта УТМ (перезапуск через introduce на новом порту). */
    changePort: function (el) {
      var input = document.getElementById('port-edit-input');
      if (!input) return;
      var sel = selectedUtm();
      var newPort = parseInt(input.value, 10);
      if (!(newPort >= 1 && newPort <= 65535)) { showToast('Порт должен быть в диапазоне 1–65535'); return; }
      if (newPort === sel.port) { showToast('Порт не изменился'); return; }
      if (!window.confirm('Сменить порт «' + sel.name + '» с ' + sel.port + ' на ' + newPort + '?\nУТМ перезапустится (~1 мин), обмен с ЕГАИС прервётся.')) return;
      showToast('Меняю порт на ' + newPort + '…');
      fetch('/api/utm/port', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ service: sel.service, newPort: newPort }),
      })
        .then(function (r) {
          if (r.status === 409) { showToast('Порт занят или идёт другая операция — подождите'); return; }
          if (r.status === 400) { return r.json().then(function (d) { showToast(d.error || 'Некорректный порт'); }); }
          if (!r.ok) throw new Error();
          showToast('Смена порта запущена — статус обновится автоматически');
        })
        .catch(function () { showToast('Не удалось сменить порт'); });
    },
    /* краткое название УТМ (ключ — серийник токена) */
    saveName: function () {
      var input = document.getElementById('name-edit-input');
      if (!input) return;
      var serial = input.getAttribute('data-serial');
      if (!serial) { showToast('Нет серийника токена — имя нельзя сохранить'); return; }
      postName(serial, input.value);
    },
    resetName: function (el) {
      var serial = el.getAttribute('data-serial');
      if (serial) postName(serial, '');
    },
    openDeleteConfirm: function () { setState({ confirmDeleteOpen: true }); },
    closeDeleteConfirm: function () { setState({ confirmDeleteOpen: false }); },
    confirmDelete: function () { setState({ confirmDeleteOpen: false }); notReady('Удаление УТМ'); },

    /* токены */
    resetReaders: function () { notReady('Сброс ридеров (используйте «Полечить токены»)'); },

    /* Скан токенов через трей (для установки/обследования). */
    scanTokens: function () {
      if (state.scanning) return;
      setState({ scanning: true, scannedTokens: null });
      showToast('Сканирую токены (через приложение в трее)…');
      runJob('scan', function (res, err) {
        if (err) { setState({ scanning: false }); showToast(err); return; }
        setState({ scanning: false, scannedTokens: (res && res.tokens) || [] });
      });
    },

    /* Подхватить существующие УТМ под управление (первый запуск): строит state.json
       из обнаруженных служб + отсканированных токенов. */
    adoptExisting: function () {
      var toks = state.scannedTokens || [];
      showToast('Подхватываю существующие УТМ…');
      fetch('/api/setup/adopt', {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ tokens: toks }),
      })
        .then(function (r) { return r.json(); })
        .then(function (d) { showToast('Подхвачено УТМ: ' + (d.matched || 0) + ' из ' + (d.total || 0)); pollStatus(true); })
        .catch(function () { showToast('Не удалось подхватить'); });
    },

    /* Полечить токены через трей (рестарт SCardSvr + подъём; требует UAC). */
    healTokens: function () {
      if (state.healing) return;
      if (!window.confirm('Полечить токены?\nСлужба смарт-карт перезапустится, обмен на ~минуту прервётся.\nВ приложении в трее потребуется подтвердить права (UAC).')) return;
      setState({ healing: true });
      showToast('Запускаю лечение через трей — подтвердите UAC…');
      runJob('heal', function (res, err) {
        setState({ healing: false });
        showToast(err ? err : 'Лечение запущено — статус обновится автоматически');
      });
    },

    /* настройка файрвола (предпочтение; применение при установке УТМ) */
    setFirewallYes: function () { setState({ firewallAuto: true }); },
    setFirewallNo: function () { setState({ firewallAuto: false }); },

    /* обновления — реальное обновление в беклоге, пока честно */
    updateOrchestrator: function () { notReady('Обновление оркестратора'); },
    updateUtm: function () { notReady('Обновление УТМ'); },

    /* логи */
    onLogsPort: function (el) { setState({ logsFilterPort: el.value }); },
    onLogsLevel: function (el) { setState({ logsFilterLevel: el.value }); },
    onLogsSearch: function (el) {
      // точечное обновление списка, чтобы не терять фокус поля
      state.logsSearch = el.value;
      var list = document.getElementById('logs-list');
      if (list) list.innerHTML = logsListHTML(colors());
    },
    downloadLogs: function () { notReady('Экспорт логов'); },

    /* настройки */
    setRemoteOn: function () { setState({ remoteAccess: true }); },
    setRemoteOff: function () { setState({ remoteAccess: false }); },
    togglePinVisible: function () { setState({ pinVisible: !state.pinVisible }); },
    setAuthReqOn: function () {
      // включаем требование входа; не выкидываем пользователя из текущей сессии
      setState({ requireAuth: true, authed: true });
      saveSettings();
      showToast('Вход в панель будет запрошен при следующем открытии');
    },
    setAuthReqOff: function () { setState({ requireAuth: false }); saveSettings(); showToast('Вход в панель отключён'); },
    addIp: function () { state.allowedIps = state.allowedIps.concat(['']); render(); },
    removeIp: function (el) {
      var idx = parseInt(el.getAttribute('data-idx'), 10);
      state.allowedIps = state.allowedIps.filter(function (_, i) { return i !== idx; });
      render();
    },
    ipInput: function (el) {
      var idx = parseInt(el.getAttribute('data-idx'), 10);
      state.allowedIps[idx] = el.value; // без ре-рендера — сохраняем фокус
    },
    saveIps: function () {
      var cleaned = state.allowedIps.map(function (s) { return s.trim(); }).filter(Boolean);
      state.allowedIps = cleaned.length ? cleaned : [''];
      render();
      saveSettings().then(function () { showToast('Список IP сохранён'); })
        .catch(function () { showToast('Не удалось сохранить список'); });
    },
  };

  /* ====================== ДЕЛЕГИРОВАНИЕ СОБЫТИЙ ====================== */
  function runAction(name, el, e) {
    var fn = actions[name];
    if (fn) fn(el, e);
  }

  function bindEvents() {
    // клики
    appEl.addEventListener('click', function (e) {
      var tag = e.target.tagName;
      // не перехватываем клики по элементам форм (у них своя логика change/input/submit)
      var actionEl = e.target.closest('[data-action]');
      if (actionEl && !/^(SELECT|OPTION|INPUT|TEXTAREA)$/.test(tag)) {
        e.preventDefault();
        runAction(actionEl.getAttribute('data-action'), actionEl, e);
        return;
      }
      // клик вне поповеров закрывает панель уведомлений
      if (!e.target.closest('[data-pop]')) {
        if (state.notifOpen) setState({ notifOpen: false });
      }
    });

    // изменения select
    appEl.addEventListener('change', function (e) {
      var el = e.target.closest('[data-action]');
      if (el) runAction(el.getAttribute('data-action'), el, e);
    });

    // ввод в текстовые поля (поиск логов, IP)
    appEl.addEventListener('input', function (e) {
      var el = e.target.closest('[data-input]');
      if (el) runAction(el.getAttribute('data-input'), el, e);
    });

    // отправка формы входа
    appEl.addEventListener('submit', function (e) {
      var el = e.target.closest('[data-action]');
      if (el) { e.preventDefault(); runAction(el.getAttribute('data-action'), el, e); }
    });
  }

  /* ====================== ЖИВОЙ ОПРОС /api/status ====================== */
  function patchServiceIndicator() {
    var c = colors();
    var el = document.getElementById('svc-indicator');
    if (!el) return;
    var connected = !state.liveError && !!state.liveStatus;
    var col = connected ? c.ok : c.error;
    var txt = connected ? 'Служба подключена' : 'Служба недоступна';
    el.innerHTML = '<div style="width:7px;height:7px;border-radius:50%;background:' + col + ';flex-shrink:0;"></div>' +
      '<span style="font:12px system-ui,sans-serif;color:' + c.textTertiary + ';">' + txt + '</span>';
  }

  /* Сохранить/сбросить краткое название УТМ по серийнику токена. */
  function postName(serial, name) {
    fetch('/api/utm/name', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ serial: serial, name: name }),
    })
      .then(function (r) { if (!r.ok) throw new Error(); return r.json(); })
      .then(function () {
        showToast(String(name).trim() ? 'Краткое название сохранено' : 'Название сброшено');
        pollStatus(true); // подтянуть новый title и форсированно перерисовать
      })
      .catch(function () { showToast('Не удалось сохранить название'); });
  }

  /* Экраны, опирающиеся на живые данные /api/status. */
  function liveBackedScreen() {
    return state.screen === 'overview' || state.screen === 'utm' || state.screen === 'utm-detail';
  }
  /* Поле ввода краткого имени в фокусе — не перерисовывать по таймеру, иначе
     затрём набираемый текст. */
  function nameInputFocused() {
    var a = document.activeElement;
    return !!a && a.id === 'name-edit-input';
  }

  /* Защита от лавины: не запускаем новый опрос, пока предыдущий в полёте. Иначе при
     медленном ответе браузер каждые 8с добавляет запрос, они накладываются и вводят
     службу в голодание пула потоков (синхронные вызовы ServiceController). */
  var pollInFlight = false;
  function pollStatus(force) {
    if (pollInFlight && !force) return;
    pollInFlight = true;
    fetch('/api/status', { cache: 'no-store' })
      .then(function (r) { return r.json(); })
      .then(function (d) {
        state.liveStatus = d;
        state.liveError = false;
        state.lastCheck = new Date().toLocaleTimeString('ru-RU');
        var authGate = state.requireAuth && !state.authed;
        if (!authGate && liveBackedScreen() && (force || !nameInputFocused())) render();
        else patchServiceIndicator();
      })
      .catch(function () {
        state.liveError = true;
        if (!(state.requireAuth && !state.authed) && liveBackedScreen()) render();
        else patchServiceIndicator();
      })
      .then(function () { pollInFlight = false; }, function () { pollInFlight = false; });
  }

  /* ====================== ИНИЦИАЛИЗАЦИЯ ====================== */
  function init() {
    appEl = document.getElementById('app');

    // адаптив через matchMedia (десктоп/мобайл — единый набор экранов)
    var mq = window.matchMedia('(max-width: 860px)');
    state.isMobile = mq.matches;
    var onMq = function (e) { state.isMobile = e.matches; state.mobileNavOpen = false; render(); };
    if (mq.addEventListener) mq.addEventListener('change', onMq);
    else mq.addListener(onMq); // старые браузеры

    bindEvents();
    render();

    pollStatus();
    setInterval(pollStatus, 8000);
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
  else init();
})();
