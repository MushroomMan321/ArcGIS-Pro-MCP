/*
  Renderer half of the Claude dock pane.

  The add-in owns the Claude Code process and its pseudo console; this file owns
  nothing but pixels and keystrokes. The two halves talk over WebView2 host
  messaging with a small JSON protocol:

    host -> page   init, output, overlay, reset, focus
    page -> host   hello, ready, input, inputBytes, resize, link

  Startup is a handshake rather than a race: the page says `hello`, the host
  replies with `init` carrying the theme and font, and only once the terminal has
  been created and measured does the page send `ready` with its real dimensions.
  The host waits for that before spawning Claude Code, so the process never sees
  a bogus 80x24 first and then immediately gets resized.
*/
(function () {
  "use strict";

  var host = window.chrome && window.chrome.webview;
  var terminalElement = document.getElementById("terminal");
  var overlayElement = document.getElementById("overlay");
  var overlayTitle = document.getElementById("overlay-title");
  var overlayDetail = document.getElementById("overlay-detail");

  var term = null;
  var fitAddon = null;
  var resizeTimer = 0;

  function post(message) {
    if (host) {
      host.postMessage(message);
    }
  }

  function showOverlay(title, detail) {
    overlayTitle.textContent = title || "";
    overlayDetail.textContent = detail || "";
    overlayElement.hidden = false;
  }

  function hideOverlay() {
    overlayElement.hidden = true;
  }

  function applyChrome(chrome) {
    if (!chrome) {
      return;
    }
    var root = document.documentElement;
    Object.keys(chrome).forEach(function (key) {
      root.style.setProperty("--" + key, chrome[key]);
    });
  }

  /*
    xterm measures the character cell from the live DOM, so a fit() that runs
    before the web font is resolved computes the wrong cell size and the process
    is told the wrong column count. Waiting on document.fonts avoids that.
  */
  function whenFontsReady(callback) {
    if (document.fonts && document.fonts.ready) {
      document.fonts.ready.then(callback).catch(callback);
    } else {
      callback();
    }
  }

  function fit() {
    if (!fitAddon) {
      return;
    }
    try {
      fitAddon.fit();
    } catch (error) {
      /* The pane can be measured at zero size while docked or collapsed. */
    }
  }

  function scheduleFit() {
    window.clearTimeout(resizeTimer);
    resizeTimer = window.setTimeout(fit, 60);
  }

  function copySelection() {
    var selection = term.getSelection();
    if (!selection) {
      return false;
    }
    if (navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(selection).catch(function () {});
    }
    return true;
  }

  function pasteFromClipboard() {
    if (!navigator.clipboard || !navigator.clipboard.readText) {
      return;
    }
    navigator.clipboard
      .readText()
      .then(function (text) {
        if (text) {
          term.paste(text);
        }
      })
      .catch(function () {});
  }

  /*
    Terminal key conventions, matching Windows Terminal: Ctrl+C keeps its normal
    meaning (interrupt) unless there is a selection to copy, and the explicit
    Ctrl+Shift+C / Ctrl+Shift+V pair always does clipboard work. Returning false
    tells xterm we handled the key and it should not be sent to the process.
  */
  function handleKey(event) {
    if (event.type !== "keydown" || !event.ctrlKey) {
      return true;
    }

    var key = event.key.toLowerCase();

    if (event.shiftKey && key === "c") {
      copySelection();
      return false;
    }
    if (event.shiftKey && key === "v") {
      pasteFromClipboard();
      return false;
    }
    if (!event.shiftKey && key === "c") {
      return !copySelection();
    }
    if (key === "insert") {
      if (event.shiftKey) {
        pasteFromClipboard();
      } else {
        copySelection();
      }
      return false;
    }

    return true;
  }

  /*
    Claude Code prints URLs and OSC 8 hyperlinks. Opening them inside the pane
    would replace the terminal, and window.open is blocked by the page's CSP, so
    hand every activation to the add-in and let it use the default browser.
  */
  function openLink(event, uri) {
    if (event && event.preventDefault) {
      event.preventDefault();
    }
    post({ kind: "link", url: uri });
  }

  function toBase64(binaryString) {
    return window.btoa(binaryString);
  }

  function createTerminal(message) {
    var options = {
      cursorBlink: true,
      cursorStyle: "bar",
      cursorWidth: 2,
      drawBoldTextInBrightColors: true,
      fontFamily: message.font.family,
      fontSize: message.font.size,
      lineHeight: message.font.lineHeight,
      letterSpacing: 0,
      scrollback: 10000,
      smoothScrollDuration: 80,
      theme: message.theme,
      linkHandler: { activate: openLink }
    };

    /*
      Telling xterm it is driven by ConPTY lets it reproduce Windows' own
      line-wrap and reflow behaviour. Without it, resizing the pane mangles
      wrapped output.
    */
    if (message.conptyBuildNumber) {
      options.windowsPty = { backend: "conpty", buildNumber: message.conptyBuildNumber };
    }

    term = new window.Terminal(options);
    fitAddon = new window.FitAddon.FitAddon();
    term.loadAddon(fitAddon);
    term.loadAddon(new window.WebLinksAddon.WebLinksAddon(openLink));
    term.attachCustomKeyEventHandler(handleKey);

    term.onData(function (data) {
      post({ kind: "input", data: data });
    });
    term.onBinary(function (data) {
      post({ kind: "inputBytes", data: toBase64(data) });
    });
    term.onResize(function (size) {
      post({ kind: "resize", cols: size.cols, rows: size.rows });
    });

    term.open(terminalElement);

    whenFontsReady(function () {
      fit();
      term.focus();
      post({ kind: "ready", cols: term.cols, rows: term.rows });
    });
  }

  function onHostMessage(event) {
    var message = event.data;
    if (!message || !message.kind) {
      return;
    }

    switch (message.kind) {
      case "init":
        applyChrome(message.chrome);
        createTerminal(message);
        break;
      case "output":
        if (term) {
          term.write(message.data);
        }
        break;
      case "reset":
        if (term) {
          term.reset();
          term.clear();
        }
        break;
      case "overlay":
        if (message.visible === false) {
          hideOverlay();
        } else {
          showOverlay(message.title, message.detail);
        }
        break;
      case "focus":
        if (term) {
          term.focus();
        }
        break;
      default:
        break;
    }
  }

  if (host) {
    host.addEventListener("message", onHostMessage);
  }

  window.addEventListener("resize", scheduleFit);
  if (window.ResizeObserver) {
    new window.ResizeObserver(scheduleFit).observe(document.body);
  }

  /* Clicking anywhere in the pane should put the caret back in the terminal. */
  document.addEventListener("mousedown", function (event) {
    if (term && event.button === 0 && overlayElement.hidden) {
      window.setTimeout(function () {
        term.focus();
      }, 0);
    }
  });

  /* Right-click copies a selection if there is one, otherwise pastes. */
  document.addEventListener("contextmenu", function (event) {
    if (!term) {
      return;
    }
    event.preventDefault();
    if (!copySelection()) {
      pasteFromClipboard();
    }
  });

  showOverlay("Starting Claude Code…", "");
  post({ kind: "hello" });
})();
