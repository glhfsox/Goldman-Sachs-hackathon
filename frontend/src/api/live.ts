import { useEffect, useRef, useState } from 'react';
import type { LiveMessage } from './types';

/**
 * Live alert updates from the API's WebSocket (/ws, contracts/api.md and events.md §5).
 * Reconnects with backoff. Messages can be lost by design, so `onReconnect` should refetch.
 */
export function useLiveFeed(onMessage: (m: LiveMessage) => void, onReconnect: () => void): boolean {
  const [connected, setConnected] = useState(false);
  const handlers = useRef({ onMessage, onReconnect });
  handlers.current = { onMessage, onReconnect };

  useEffect(() => {
    let socket: WebSocket | null = null;
    let retry = 0;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let stopped = false;
    let everOpened = false;

    const connect = () => {
      const url = `${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/ws`;
      socket = new WebSocket(url);
      socket.onopen = () => {
        setConnected(true);
        retry = 0;
        if (everOpened) handlers.current.onReconnect();
        everOpened = true;
      };
      socket.onmessage = (ev) => {
        try {
          handlers.current.onMessage(JSON.parse(ev.data as string) as LiveMessage);
        } catch {
          // Ignore anything that is not a live message.
        }
      };
      socket.onclose = () => {
        setConnected(false);
        if (stopped) return;
        retry += 1;
        timer = setTimeout(connect, Math.min(10_000, 500 * 2 ** Math.min(retry, 5)));
      };
    };

    connect();
    return () => {
      stopped = true;
      clearTimeout(timer);
      socket?.close();
    };
  }, []);

  return connected;
}
