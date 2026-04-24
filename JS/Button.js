(function () {
    if (window.__steamPlayHijackerObserver) {
        window.__steamPlayHijackerObserver.disconnect();
    }
    const oldStyles = document.getElementById('hijacker-focus-styles');
    if (oldStyles) oldStyles.remove();
    document.querySelectorAll('[data-hijacked]').forEach(el => {
        delete el.dataset.hijacked;
        el.classList.remove('injected-play-btn', '_1_Bo2Ied5s2Od4YKYTOsau', 'Play');
        el.style = "";
    });
    const isBigPictureMode = document.title === "Steam Big Picture Mode";
    function injectStyles() {
        const style = document.createElement('style');
        style.id = 'hijacker-focus-styles';
        let css = `.injected-play-btn { transition: all 0.2s ease-out !important; }`;
        if (isBigPictureMode) {
            css += `
                .injected-play-btn {
                    background-color: rgba(0, 0, 0, 0) !important;
                    background-image: none !important;
                    border: 1px solid rgba(255, 255, 255, 0.2) !important;
                }
            `;
        } else {
            css += `
                .injected-play-btn {
                    background: linear-gradient(to right, rgb(112, 214, 29) 0%, rgb(1, 167, 91) 60%) !important;
                    background-size: 330% 100% !important;
                    color: white !important;
                }
            `;
        }
        css += `
            .injected-play-btn:focus, .injected-play-btn:focus-within, .injected-play-btn:hover {
                background: #ff8c00 !important; 
                background-image: none !important;
                color: #fff !important;
                outline: none !important;
                box-shadow: 0 0 12px rgba(255, 140, 0, 0.7) !important;
                border: 1px solid rgba(255, 255, 255, 0.5) !important;
            }
            .injected-play-btn:active {
                background-position: 40% 50% !important;
                box-shadow: inset 0px 2px 4px rgba(0, 0, 0, 0.5) !important;
            }
        `;
        style.innerHTML = css;
        document.head.appendChild(style);
    }
    function getGlobalSteamAppId() {
        const trackingDiv = document.querySelector('.MZPxEZcxAGtPDf1NYtouS');
        if (!trackingDiv) return null;
        try {
            const match = decodeURIComponent(trackingDiv.innerHTML || trackingDiv.textContent).match(/\/library\/app\/(\d+)/);
            return match ? match[1] : null;
        } catch (e) { return null; }
    }
    function getContextualGameInfo(element) {
        let appId = null;
        let gameName = "Unknown Game";
        if (element.classList.contains('contextMenuItem') && window.__lastRightClickedInfo) {
            return window.__lastRightClickedInfo;
        }
        const parentLink = element.closest('a[href*="/app/"], a[href*="steam://nav/games/details/"]');
        if (parentLink) appId = parentLink.href.match(/\/(?:app|details)\/(\d+)/)?.[1];
        let current = element;
        const ignoreText = ['play', 'purchase', 'time', 'last', 'today', 'total', 'manage', 'controller', 'news', 'properties', 'favorites', 'add to'];
        for (let i = 0; i < 15 && current && current !== document.body; i++) {
            if (!appId) {
                const images = current.querySelectorAll('img[src]');
                for (let img of images) {
                    const bpmCustomMatch = img.src.match(/\/customimages\/(\d+)(?:_logo|_hero)?\./i);
                    const bpmAssetMatch = img.src.match(/\/community_assets\/images\/apps\/(\d+)\
                    const standardAsset = img.src.match(/\/(?:apps|assets)\/(\d+)\
                    const fileMatch = img.src.match(/\/(\d+)\.(?:jpg|jpeg|png|webp)/i);
                    const m = bpmCustomMatch || bpmAssetMatch || standardAsset || fileMatch;
                    if (m) { appId = m[1]; break; }
                }
            }
            if (gameName === "Unknown Game") {
                const spans = current.querySelectorAll('span');
                for (let span of spans) {
                    const text = span.textContent.trim();
                    if (text.length > 1 && !ignoreText.some(word => text.toLowerCase().includes(word))) {
                        gameName = text; break;
                    }
                }
            }
            if (appId && gameName !== "Unknown Game") break;
            current = current.parentElement;
        }
        return { appId: appId || getGlobalSteamAppId(), gameName };
    }
    function sendPlayCommand(gameInfo, source) {
        const { appId, gameName } = gameInfo;
        if (!appId) return;
        const time = new Date().toLocaleTimeString('en-US', { hour12: true, hour: 'numeric', minute: 'numeric', second: 'numeric' });
        console.log(`[Steam Bridge] Handoff (${source}): Play ${appId} | Name: ${gameName} | Time: ${time}`);
        let cmdEl = document.getElementById('steam-integration-commands');
        if (!cmdEl) {
            cmdEl = document.createElement('div');
            cmdEl.id = 'steam-integration-commands';
            cmdEl.style.display = 'none';
            document.body.appendChild(cmdEl);
        }
        if (cmdEl) {
            cmdEl.innerText = `Play ${appId} (${time})`;
        }
    }
    function hijackButtons() {
        document.querySelectorAll('._33cnXIqTRgRr49_FNXIHj6').forEach(div => {
            if (div.textContent.trim() === 'Purchase') {
                const btn = div.closest('[role="button"]');
                if (!btn || btn.dataset.hijacked) return;
                btn.dataset.hijacked = "true";
                btn.classList.add('injected-play-btn');
                div.textContent = 'PLAY';
                const svg = btn.querySelector('svg');
                if (svg) {
                    svg.setAttribute('viewBox', '0 0 256 256');
                    svg.innerHTML = `<path d="M65.321,33.521c-11.274-6.615-20.342-1.471-20.342,11.52V210.96c0,12.989,9.068,18.135,20.342,11.521l137.244-82.348 c11.274-6.618,11.274-17.646,0-24.509L65.321,33.521z" fill="currentColor"></path>`;
                }
                btn.addEventListener('click', (e) => {
                    e.preventDefault(); e.stopPropagation();
                    sendPlayCommand(getContextualGameInfo(btn), "Main Button");
                }, { capture: true });
            }
        });
        document.querySelectorAll('._3AjoLnMNKxYmNTGTJCLfgs._3VOR2AeYATx3qSE0I-Pm-5:not(._1_Bo2Ied5s2Od4YKYTOsau)').forEach(btn => {
            if (btn.dataset.hijacked) return;
            const svg = btn.querySelector('svg');
            if (svg) {
                btn.dataset.hijacked = "true";
                btn.classList.add('_1_Bo2Ied5s2Od4YKYTOsau', 'injected-play-btn');
                svg.setAttribute('viewBox', '0 0 256 256');
                svg.innerHTML = `<path d="M65.321,33.521c-11.274-6.615-20.342-1.471-20.342,11.52V210.96c0,12.989,9.068,18.135,20.342,11.521l137.244-82.348 c11.274-6.618,11.274-17.646,0-24.509L65.321,33.521z" fill="currentColor"></path>`;
                btn.addEventListener('click', (e) => {
                    e.preventDefault(); e.stopPropagation();
                    sendPlayCommand(getContextualGameInfo(btn), "Library Tile");
                }, { capture: true });
            }
        });
        document.querySelectorAll('.contextMenuItem.PurchaseApp, [role="menuitem"].PurchaseApp').forEach(btn => {
            if (btn.dataset.hijacked) return;
            btn.dataset.hijacked = "true";
            btn.classList.remove('PurchaseApp');
            btn.classList.add('Play', 'injected-play-btn');
            btn.innerHTML = `<svg viewBox="0 0 256 256" style="width:16px;height:16px;margin-right:8px;"><path d="M65.321,33.521c-11.274-6.615-20.342-1.471-20.342,11.52V210.96c0,12.989,9.068,18.135,20.342,11.521l137.244-82.348 c11.274-6.618,11.274-17.646,0-24.509L65.321,33.521z" fill="currentColor"></path></svg>Play`;
            btn.addEventListener('click', (e) => {
                e.preventDefault(); e.stopPropagation();
                sendPlayCommand(getContextualGameInfo(btn), "Context Menu");
                document.body.click();
            }, { capture: true });
        });
    }
    window.__lastRightClickedInfo = null;
    document.addEventListener('contextmenu', (e) => {
        window.__lastRightClickedInfo = getContextualGameInfo(e.target);
    }, true);
    injectStyles();
    hijackButtons();
    const observer = new MutationObserver(() => hijackButtons());
    observer.observe(document.body, { childList: true, subtree: true });
    window.__steamPlayHijackerObserver = observer;
})();
