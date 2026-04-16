(function () {
  const inflightRequests = new Map();
  const fetchCachePrefix = 'imajinationFetchCache:';
  const defaultApiCacheTtlMs = 30 * 1000;
  let csrfToken = null;
  let csrfTokenPromise = null;
  let sessionTimeoutHandle = null;
  let sessionMonitorHandle = null;
  const sessionIdleLimitMs = 30 * 60 * 1000;
  const sessionMonitorIntervalMs = 45 * 1000;

  function isSameOriginApiRequest(url) {
    try {
      const parsed = new URL(url, window.location.origin);
      return parsed.origin === window.location.origin && parsed.pathname.startsWith('/api/');
    } catch {
      return false;
    }
  }

  function shouldCacheRequest(url, options) {
    const method = (options?.method || 'GET').toUpperCase();
    if (method !== 'GET') return false;

    try {
      const parsed = new URL(url, window.location.origin);
      return parsed.origin === window.location.origin
        && parsed.pathname.startsWith('/api/')
        && parsed.pathname !== '/api/security/csrf-token'
        && parsed.pathname !== '/api/auth/session-status';
    } catch {
      return false;
    }
  }

  function buildCacheKey(url) {
    return `${fetchCachePrefix}${url}`;
  }

  function readCachedResponse(url) {
    try {
      const raw = sessionStorage.getItem(buildCacheKey(url));
      if (!raw) return null;

      const parsed = JSON.parse(raw);
      if (!parsed || typeof parsed.savedAt !== 'number' || typeof parsed.body !== 'string') return null;
      if (Date.now() - parsed.savedAt > defaultApiCacheTtlMs) return null;

      return parsed;
    } catch {
      return null;
    }
  }

  function writeCachedResponse(url, response, bodyText) {
    try {
      sessionStorage.setItem(buildCacheKey(url), JSON.stringify({
        savedAt: Date.now(),
        body: bodyText,
        status: response.status,
        statusText: response.statusText,
        headers: Array.from(response.headers.entries())
      }));
    } catch {
      // Ignore storage pressure and keep the app working.
    }
  }

  function createResponseFromCache(cached) {
    return new Response(cached.body, {
      status: cached.status || 200,
      statusText: cached.statusText || 'OK',
      headers: cached.headers || { 'Content-Type': 'application/json' }
    });
  }

  function installSecurityFetch() {
    if (window.__imajinationSecurityFetchInstalled) return;
    window.__imajinationSecurityFetchInstalled = true;

    const nativeFetch = window.fetch.bind(window);

    async function ensureCsrfToken() {
      if (csrfToken) return csrfToken;
      if (csrfTokenPromise) return csrfTokenPromise;

      csrfTokenPromise = nativeFetch('/api/security/csrf-token', {
        method: 'GET',
        credentials: 'same-origin',
        headers: {
          'X-Requested-With': 'fetch'
        }
      })
        .then(async (response) => {
          if (!response.ok) {
            throw new Error('Failed to initialize request security.');
          }

          const data = await response.json().catch(() => ({}));
          csrfToken = data.token || '';
          return csrfToken;
        })
        .finally(() => {
          csrfTokenPromise = null;
        });

      return csrfTokenPromise;
    }

    window.fetch = async function securedFetch(resource, options = {}) {
      const originalUrl = typeof resource === 'string' ? resource : resource?.url || '';
      const method = (options?.method || (resource instanceof Request ? resource.method : 'GET') || 'GET').toUpperCase();
      const isApiRequest = isSameOriginApiRequest(originalUrl);
      const requiresCsrf = !['GET', 'HEAD', 'OPTIONS', 'TRACE'].includes(method) && isApiRequest;
      const headers = new Headers(resource instanceof Request ? resource.headers : undefined);

      if (options.headers) {
        new Headers(options.headers).forEach((value, key) => headers.set(key, value));
      }

      if (isApiRequest) {
        if (requiresCsrf) {
          const token = await ensureCsrfToken();
          if (!headers.has('X-CSRF-TOKEN') && token) {
            headers.set('X-CSRF-TOKEN', token);
          }
        }

        const actorUserId = localStorage.getItem('userId');
        const actorRole = localStorage.getItem('userRole');
        const sessionToken = localStorage.getItem('sessionToken');
        const accessToken = localStorage.getItem('accessToken');

        if (actorUserId && !headers.has('X-Actor-UserId')) {
          headers.set('X-Actor-UserId', actorUserId);
        }
        if (actorRole && !headers.has('X-Actor-Role')) {
          headers.set('X-Actor-Role', actorRole);
        }
        if (sessionToken && !headers.has('X-Session-Token')) {
          headers.set('X-Session-Token', sessionToken);
        }
        if (accessToken && !headers.has('Authorization')) {
          headers.set('Authorization', `Bearer ${accessToken}`);
        }
      }

      const finalOptions = {
        ...options,
        headers,
        credentials: options.credentials || 'same-origin'
      };

      const finalResource = resource instanceof Request
        ? new Request(resource, finalOptions)
        : resource;

      return nativeFetch(finalResource, finalOptions);
    };
  }

  function installFetchOptimizer() {
    if (window.__imajinationFetchOptimized) return;
    window.__imajinationFetchOptimized = true;

    const nativeFetch = window.fetch.bind(window);

    window.fetch = async function optimizedFetch(resource, options = {}) {
      const url = typeof resource === 'string' ? resource : resource?.url || '';
      const canCache = shouldCacheRequest(url, options) && options.cache !== 'no-store';

      if (!canCache) {
        return nativeFetch(resource, options);
      }

      const cacheKey = `${(options.method || 'GET').toUpperCase()}:${url}`;
      if (inflightRequests.has(cacheKey)) {
        return inflightRequests.get(cacheKey).then(response => response.clone());
      }

      const cached = readCachedResponse(url);
      if (cached) {
        const cachedResponse = createResponseFromCache(cached);
        const revalidatePromise = nativeFetch(resource, options)
          .then(async response => {
            if (response.ok) {
              const clone = response.clone();
              const body = await clone.text();
              writeCachedResponse(url, response, body);
            }
            return response;
          })
          .catch(() => null)
          .finally(() => inflightRequests.delete(cacheKey));

        inflightRequests.set(cacheKey, revalidatePromise);
        return cachedResponse;
      }

      const requestPromise = nativeFetch(resource, options)
        .then(async response => {
          if (response.ok) {
            const clone = response.clone();
            const body = await clone.text();
            writeCachedResponse(url, response, body);
            return new Response(body, {
              status: response.status,
              statusText: response.statusText,
              headers: response.headers
            });
          }
          return response;
        })
        .finally(() => inflightRequests.delete(cacheKey));

      inflightRequests.set(cacheKey, requestPromise);
      const response = await requestPromise;
      return response.clone();
    };
  }

  function optimizeImages() {
    const images = document.querySelectorAll('img');
    images.forEach((img, index) => {
      if (!img.getAttribute('loading')) {
        img.setAttribute('loading', index < 2 ? 'eager' : 'lazy');
      }
      if (!img.getAttribute('decoding')) {
        img.setAttribute('decoding', 'async');
      }
      if (!img.getAttribute('fetchpriority') && index > 1) {
        img.setAttribute('fetchpriority', 'low');
      }
    });
  }

  function optimizeSections() {
    document.querySelectorAll('section, article, aside').forEach((element, index) => {
      if (index > 1 && !element.style.contentVisibility) {
        element.style.contentVisibility = 'auto';
        element.style.containIntrinsicSize = '1px 600px';
      }
    });
  }

  function installIdleSessionTimeout() {
    if (window.__imajinationSessionTimeoutInstalled) return;
    window.__imajinationSessionTimeoutInstalled = true;

    const hasSession = !!localStorage.getItem('userId');
    const path = window.location.pathname.toLowerCase();
    const isAuthPage = path.includes('/pages/auth/');
    if (!hasSession || isAuthPage) return;

    const resetTimer = () => {
      if (sessionTimeoutHandle) {
        window.clearTimeout(sessionTimeoutHandle);
      }

      sessionTimeoutHandle = window.setTimeout(() => {
        localStorage.clear();
        localStorage.setItem('sessionTimedOut', '1');
        window.location.href = '/pages/auth/login.html?timeout=1';
      }, sessionIdleLimitMs);
    };

    ['click', 'keydown', 'mousemove', 'scroll', 'touchstart'].forEach((eventName) => {
      window.addEventListener(eventName, resetTimer, { passive: true });
    });

    resetTimer();
  }

  function clearStoredUserSession(reasonKey) {
    localStorage.removeItem('userId');
    localStorage.removeItem('userFirstName');
    localStorage.removeItem('userRole');
    localStorage.removeItem('username');
    localStorage.removeItem('userProfilePicture');
    localStorage.removeItem('sessionToken');
    localStorage.removeItem('accessToken');
    if (reasonKey) {
      localStorage.setItem('sessionEndedReason', reasonKey);
    }
  }

  function installSessionMonitor() {
    if (window.__imajinationSessionMonitorInstalled) return;
    window.__imajinationSessionMonitorInstalled = true;

    const path = window.location.pathname.toLowerCase();
    const isAuthPage = path.includes('/pages/auth/');
    if (isAuthPage) return;

    const userId = (localStorage.getItem('userId') || '').trim();
    const sessionToken = (localStorage.getItem('sessionToken') || '').trim();
    if (!userId || !sessionToken) return;

    const nativeFetch = window.fetch.bind(window);

    const validateSession = async () => {
      try {
        const response = await nativeFetch(`/api/auth/session-status?userId=${encodeURIComponent(userId)}`, {
          method: 'GET',
          cache: 'no-store',
          credentials: 'same-origin',
          headers: {
            'X-Requested-With': 'fetch',
            'X-Session-Token': sessionToken
          }
        });

        if (response.ok) {
          return;
        }

        const data = await response.json().catch(() => ({}));
        const replacedElsewhere = !!data?.replacedElsewhere;
        clearStoredUserSession(replacedElsewhere ? 'another_device' : 'session_ended');
        window.location.href = replacedElsewhere
          ? '/pages/auth/login.html?session=another-device'
          : '/pages/auth/login.html?session=expired';
      } catch {
        // Keep the current session active if the network briefly fails.
      }
    };

    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'visible') {
        validateSession();
      }
    });

    sessionMonitorHandle = window.setInterval(validateSession, sessionMonitorIntervalMs);
  }

  function initPerformanceHelpers() {
    installSecurityFetch();
    installFetchOptimizer();
    installIdleSessionTimeout();
    installSessionMonitor();
    optimizeImages();
    optimizeSections();
  }

  window.initImajinationPerformanceHelpers = initPerformanceHelpers;

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', initPerformanceHelpers, { once: true });
  } else {
    initPerformanceHelpers();
  }
})();
