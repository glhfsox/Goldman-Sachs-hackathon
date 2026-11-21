// Tells the rail's Proposals badge to refetch the open count: after a proposal is filed, accepted or rejected,
// and on live timeline messages (that is how a proposal filed by the ai-worker shows up).
const EVENT = 'alpin:proposals-changed';

export function proposalsChanged(): void {
  window.dispatchEvent(new Event(EVENT));
}

export function onProposalsChanged(listener: () => void): () => void {
  window.addEventListener(EVENT, listener);
  return () => window.removeEventListener(EVENT, listener);
}
