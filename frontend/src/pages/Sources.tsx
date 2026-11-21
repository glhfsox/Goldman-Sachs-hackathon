import { useEffect, useMemo, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { api, errorText } from '../api/client';
import type { Mapping, SchemaSample, Source } from '../api/types';
import { Plus } from '../components/Icons';
import { Shell } from '../components/Shell';
import { Pill, Switch } from '../components/Switch';
import { useToast } from '../components/Toast';
import { evaluate } from '../lib/conditions';
import { checkForm, formFromMapping, mappingFromForm, matchForSample, severityOf, type Form } from '../lib/mapping';
import { slug } from '../lib/ruleForm';
import { useLoad } from '../lib/useLoad';
import { ago, cap, SEVERITY } from '../lib/ui';

const EMPTY: Form = { type: '', severity: '', service: '', env: '', ts: '', dedup: '' };

function mappingFor(sample: SchemaSample, mappings: Mapping[]): Mapping | null {
  const envelope = { source: sample.source_id, raw: sample.sample as Record<string, unknown> };
  return [...mappings].filter((m) => m.status === 'active').sort((a, b) => b.priority - a.priority).find((m) => {
    try {
      return evaluate(m.match, envelope);
    } catch {
      return false;
    }
  }) ?? null;
}

function shapeName(s: SchemaSample): string {
  const x = s.sample as Record<string, any>;
  if (x && typeof x === 'object') {
    const kind = x.kind ?? x.type ?? x.meta?.schema ?? x.alertname ?? x.event ?? x.job;
    if (kind) return String(kind);
    return Object.keys(x).slice(0, 3).join(', ');
  }
  return 'Text line';
}

export function SourcesPage() {
  const say = useToast();
  const [params, setParams] = useSearchParams();
  const sources = useLoad(() => api.sources(), []);
  const samples = useLoad(() => api.samples(), []);
  const mappings = useLoad(() => api.mappings(), []);
  const sel = params.get('sel') || '';
  const [form, setForm] = useState<Form>(EMPTY);
  const [saving, setSaving] = useState(false);
  const [newSrc, setNewSrc] = useState({ kind: 'http' as Source['kind'], id: '', topic: '' });

  const shapes = useMemo(() => {
    const order = { failed: 0, pending: 1, mapped: 2 } as const;
    return [...(samples.data ?? [])].sort((a, b) => order[a.status] - order[b.status] || b.created_at.localeCompare(a.created_at));
  }, [samples.data]);
  const shape = sel.startsWith('shape:') ? shapes.find((s) => s.fingerprint === sel.slice(6)) ?? null : null;
  const source = sel.startsWith('source:') ? (sources.data ?? []).find((s) => s.id === sel.slice(7)) ?? null : null;
  const isNew = sel === 'new';
  const mode: 'shape' | 'source' | 'new' | 'none' = shape ? 'shape' : source ? 'source' : isNew ? 'new' : 'none';
  const existing = shape ? mappingFor(shape, mappings.data ?? []) : null;

  useEffect(() => {
    if (!sel && shapes[0]) setParams({ sel: `shape:${shapes[0].fingerprint}` }, { replace: true });
    else if (!sel && sources.data?.[0]) setParams({ sel: `source:${sources.data[0].id}` }, { replace: true });
  }, [sel, shapes, sources.data, setParams]);

  useEffect(() => {
    setForm(existing ? formFromMapping(existing) : EMPTY);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [shape?.fingerprint, existing?.id]);

  const checks = shape ? checkForm(form, shape.sample) : [];
  const allOk = checks.every((c) => c.ok);
  const original = existing ? formFromMapping(existing) : EMPTY;
  const edited = Object.keys(form).some((k) => form[k] !== original[k]);
  const get = (k: string) => checks.find((c) => c.key === k);
  const sevWord = get('severity')?.ok ? severityOf(get('severity')!.values[0]) : null;

  const saveMapping = async () => {
    if (!shape) return;
    if (!allOk) return say('Fix the red lines first.');
    setSaving(true);
    try {
      const base = existing
        ? { id: existing.id, match: existing.match, priority: existing.priority, origin: 'human' }
        : { id: slug(`${shape.source_id}-${shapeName(shape)}`).slice(0, 40) + '-' + shape.fingerprint.slice(0, 6), match: matchForSample(shape.source_id, shape.sample), priority: 50, origin: 'human' };
      await api.saveMapping(mappingFromForm(form, shape.sample, base), shape.status === 'mapped' ? undefined : shape.fingerprint);
      say(existing ? 'Saved. New events of this shape use your version.' : 'Mapping live. The events that were waiting are processed now.');
      await Promise.all([samples.reload(), mappings.reload()]);
    } catch (e) {
      say(errorText(e));
    } finally {
      setSaving(false);
    }
  };

  const toggleSource = async (s: Source) => {
    try {
      const saved = await api.saveSource({ ...s, enabled: !s.enabled });
      sources.setData((prev) => (prev ?? []).map((x) => (x.id === s.id ? saved : x)));
      say(saved.enabled ? `${s.id} resumed.` : `${s.id} paused. It refuses new events until you resume it.`);
    } catch (e) {
      say(errorText(e));
    }
  };

  const newId = slug(newSrc.id || 'new-source');
  const newTopic = newSrc.topic || `source.${newId}`;
  const createSource = async () => {
    try {
      const s = await api.createSource({ id: newId, kind: newSrc.kind, config: newSrc.kind === 'kafka' ? { topic: newTopic } : {} });
      sources.setData((prev) => [...(prev ?? []), s]);
      setParams({ sel: `source:${s.id}` });
      setNewSrc({ kind: 'http', id: '', topic: '' });
      say(`${s.id} created. It accepts events right now.`);
    } catch (e) {
      say(errorText(e));
    }
  };

  const howTo = (kind: Source['kind'], id: string, topic: string) => kind === 'kafka'
    ? `Publish JSON messages to topic ${topic}\non the sources cluster  kafka-sources:9092\n\nOne message = one event.`
    : `curl -X POST ${location.origin.replace(':3000', ':8001')}/ingest/${id} \\\n  -H "Content-Type: application/json" \\\n  -d '{"alertname": "HighErrorRate", ...}'\n\nOne request = one event. 202 = received.`;

  const shapeTag = (s: SchemaSample) => (s.status === 'failed' ? ['needs you', '#F59E0B'] : s.status === 'pending' ? ['AI mapping', '#A1A1AA'] : ['live', '#22C55E']);
  const panel: Record<typeof mode, [string, [string, string][]]> = {
    shape: ['How new shapes work', [
      ['Nothing is lost', 'An event no mapping fits still becomes an alert straight away, marked unmapped, and waits here for a mapping (up to 1,000 events, for 1 hour).'],
      ['The AI maps it once', 'It looks at one sample and writes a mapping. If every line checks out on the sample, it goes live by itself.'],
      ['You can always change it', 'Edit any line; your version goes live when you save and every line checks out.'],
    ]],
    source: ['Good to know', [
      ['Who uses it', 'Rules of any team can match its events. The source itself belongs to no team.'],
      ['Paused', 'A paused source refuses new events until you resume it.'],
    ]],
    new: ['How fast is this?', [
      ['Under a minute', 'The source works as soon as you create it. No deploy, no restart.'],
      ['Unknown shapes are fine', 'Its events show up at once, and the AI maps new shapes in the background.'],
    ]],
    none: ['Sources', []],
  };

  return (
    <Shell side={
      <>
        <div className="head">
          <h2>Sources</h2>
          <button type="button" className={'plus' + (isNew ? ' on' : '')} aria-label="New source" title="New source" onClick={() => setParams({ sel: 'new' })}><Plus /></button>
        </div>
        <div>
          <h3>New event shapes</h3>
          {shapes.length === 0 && <div className="sub">None yet.</div>}
          {shapes.slice(0, 12).map((s) => {
            const [tag, color] = shapeTag(s);
            return (
              <button key={s.fingerprint} type="button" className={'item' + (shape?.fingerprint === s.fingerprint ? ' on' : '')} onClick={() => setParams({ sel: `shape:${s.fingerprint}` })}>
                <span className="sdot" style={{ background: color }} /><span className="grow">{shapeName(s)}</span><span style={{ fontSize: 11, opacity: 0.8 }}>{tag}</span>
              </button>
            );
          })}
        </div>
        <div>
          <h3>Receiving</h3>
          {(sources.data ?? []).map((s) => (
            <button key={s.id} type="button" className={'item' + (source?.id === s.id ? ' on' : '')} onClick={() => setParams({ sel: `source:${s.id}` })}>
              <span className="sdot" style={{ background: s.enabled ? '#22C55E' : '#A1A1AA' }} /><span className="grow">{s.id}</span><span style={{ fontSize: 11, opacity: 0.75 }}>{s.kind === 'kafka' ? 'Kafka' : 'HTTP'}</span>
            </button>
          ))}
        </div>
      </>
    }>
      <section className="content">
        {mode === 'shape' && shape && (
          <>
            <div className="row">
              {shape.status === 'mapped' ? <Pill fg="#15803D" bg="#DCFCE7">{existing?.origin === 'llm' ? 'Mapped by the AI · live' : 'Mapped · live'}</Pill>
                : shape.status === 'failed' ? <Pill fg="#92400E" bg="#FEF3C7">Needs a mapping</Pill>
                  : <Pill fg="#3F3F46" bg="#E4E4E7">The AI is mapping it</Pill>}
              <span className="small">First seen {ago(shape.created_at)} on {shape.source_id}</span>
            </div>
            <h1 className="h1 wrap">{shapeName(shape)}</h1>
            <p className="lead" style={{ color: 'var(--soft)' }}>
              {shape.status === 'mapped' ? 'This shape has a live mapping. Look it over and change any line if you want to.'
                : shape.status === 'failed' ? `The AI could not map this shape${shape.error ? `: ${shape.error}` : ''}. Its events still show up, as unmapped alerts, and wait until a mapping is active. Fill in the red lines.`
                  : 'The AI is writing a mapping for it right now. You can also write one yourself.'}
            </p>
            <div className="row" style={{ alignItems: 'flex-start', gap: 16 }}>
              <div className="dark-box" style={{ flex: '1 1 300px', minWidth: 0, borderRadius: 24 }}>
                <div className="label" style={{ color: '#A1A1AA', marginBottom: 10 }}>Sample event</div>
                <pre>{typeof shape.sample === 'string' ? shape.sample : JSON.stringify(shape.sample, null, 2)}</pre>
              </div>
              <div className="stack" style={{ flex: '1.2 1 340px', minWidth: 0, gap: 8 }}>
                <div className="label">{shape.status === 'mapped' ? 'Mapping · edit any line' : 'Mapping · fill in the red lines'}</div>
                {checks.map((c) => (
                  <div key={c.key} style={{ background: c.ok ? 'var(--grey)' : '#FEF2F2', borderRadius: 16, padding: '12px 14px', display: 'grid', gridTemplateColumns: '110px minmax(0, 1fr)', gap: 10, alignItems: 'center', boxShadow: c.ok ? 'none' : 'inset 0 0 0 2px #FCA5A5' }}>
                    <label htmlFor={`map-${c.key}`} style={{ fontWeight: 800, fontSize: 14 }}>{c.label}</label>
                    <div className="stack" style={{ gap: 4, minWidth: 0 }}>
                      <div className="row" style={{ flexWrap: 'nowrap' }}><span aria-hidden>←</span>
                        <input id={`map-${c.key}`} className="field mono" style={{ height: 36, flex: 1, fontSize: 13 }} value={form[c.key] ?? ''} placeholder='field path or "fixed value"' onChange={(e) => setForm({ ...form, [c.key]: e.target.value })} />
                      </div>
                      <span style={{ fontSize: 12, fontWeight: 600, color: c.ok ? 'var(--muted)' : 'var(--red)' }}>{c.note}</span>
                    </div>
                  </div>
                ))}
                <div className="small">A field path from the sample (like <code>involvedObject.name</code>), two joined with +, or a fixed value in quotes like "prod".</div>
              </div>
            </div>
            <div className="card" style={{ padding: '18px 20px', gap: 10 }}>
              <div className="label">The sample becomes</div>
              <div className="white row" style={{ borderRadius: 18, gap: 12, flexWrap: 'nowrap' }}>
                <span className="sev-icon" style={{ width: 40, height: 40, ...(sevWord ? { color: SEVERITY[sevWord].fg, background: SEVERITY[sevWord].bg } : { color: '#3F3F46', background: '#E4E4E7' }) }} aria-hidden>!</span>
                <div style={{ flex: 1, minWidth: 0 }}>
                  <div className="wrap" style={{ fontWeight: 800 }}>{get('type')?.ok ? get('type')!.values.join(' ') : '?'} · {get('service')?.ok ? get('service')!.values.join(' ') : '?'}</div>
                  <div className="small">{sevWord ? cap(sevWord) : 'Severity ?'} · {get('env')?.ok ? get('env')!.values.join(' ') : '?'}</div>
                </div>
              </div>
              <div style={{ fontSize: 13, fontWeight: 600, color: allOk ? '#15803D' : 'var(--red)' }}>{allOk ? 'Checked against the sample: every field was found, the severity is on our scale and the time parses. ✓' : 'Some lines do not match the sample yet.'}</div>
            </div>
            {(edited || shape.status !== 'mapped') && (
              <div className="row" style={{ justifyContent: 'flex-end' }}>
                <button type="button" className="btn light" style={{ height: 52 }} onClick={() => setForm(original)}>{existing ? 'Undo changes' : 'Reset'}</button>
                <button type="button" className="btn dark" style={{ height: 52 }} aria-disabled={!allOk} disabled={saving} onClick={() => void saveMapping()}>{existing ? 'Save changes' : 'Save and activate'}</button>
              </div>
            )}
          </>
        )}

        {mode === 'source' && source && (
          <>
            <div className="row">
              {source.enabled ? <Pill fg="#15803D" bg="#DCFCE7">Receiving</Pill> : <Pill fg="#3F3F46" bg="#F4F4F5">Paused</Pill>}
              <Pill fg="#111" bg="#F4F4F6">{source.kind === 'kafka' ? `Kafka topic ${String(source.config.topic ?? '')}` : 'HTTP webhook'}</Pill>
            </div>
            <h1 className="h1">{source.id}</h1>
            <div className="card" style={{ padding: '18px 20px', gap: 10 }}>
              <div className="label">How to send events here</div>
              <div className="dark-box" style={{ borderRadius: 16 }}><pre>{howTo(source.kind, source.id, String(source.config.topic ?? ''))}</pre></div>
            </div>
            <div className="row" style={{ gap: 12 }}>
              <Switch on={source.enabled} label="Receiving" onChange={() => void toggleSource(source)} />
              <span style={{ fontWeight: 700 }}>{source.enabled ? 'Receiving events' : 'Paused'}</span>
            </div>
          </>
        )}

        {mode === 'new' && (
          <>
            <h1 className="h1">New source</h1>
            <p className="lead">Any system can send us events. Pick how it sends, give it a name, and point it at the address below.</p>
            <div className="card">
              <span style={{ fontSize: 14, fontWeight: 700 }}>How does it send events?</span>
              <div role="group" aria-label="Kind" className="row">
                {(['http', 'kafka'] as const).map((k) => <button key={k} type="button" aria-pressed={newSrc.kind === k} className={'chip' + (newSrc.kind === k ? ' on' : '')} onClick={() => setNewSrc({ ...newSrc, kind: k })}>{k === 'http' ? 'HTTP webhook' : 'Kafka topic'}</button>)}
              </div>
              <div className="stack" style={{ gap: 6 }}><label htmlFor="srcname" style={{ fontSize: 14, fontWeight: 700 }}>Name</label><input id="srcname" className="field" placeholder="e.g. deploy-pipeline" value={newSrc.id} onChange={(e) => setNewSrc({ ...newSrc, id: e.target.value })} /><span className="small">Its id will be <code>{newId}</code>.</span></div>
              {newSrc.kind === 'kafka' && <div className="stack" style={{ gap: 6 }}><label htmlFor="topic" style={{ fontSize: 14, fontWeight: 700 }}>Topic</label><input id="topic" className="field" value={newTopic} onChange={(e) => setNewSrc({ ...newSrc, topic: e.target.value })} /></div>}
            </div>
            <div className="card" style={{ padding: '18px 20px', gap: 10 }}>
              <div className="label">Then send it like this</div>
              <div className="dark-box" style={{ borderRadius: 16 }}><pre>{howTo(newSrc.kind, newId, newTopic)}</pre></div>
            </div>
            <div className="row" style={{ justifyContent: 'flex-end' }}>
              <button type="button" className="btn light" style={{ height: 52 }} onClick={() => setParams({})}>Cancel</button>
              <button type="button" className="btn dark" style={{ height: 52 }} onClick={() => void createSource()}>Create source</button>
            </div>
          </>
        )}
        {mode === 'none' && <div className="empty">{sources.loading ? 'Loading…' : 'Pick a source or a new event shape.'}</div>}
      </section>

      <aside className="panel" aria-label="Details">
        <h2 className="h3" style={{ fontSize: 18 }}>{panel[mode][0]}</h2>
        {panel[mode][1].map(([t, x]) => (
          <div key={t} className="tile"><div style={{ fontWeight: 800, fontSize: 15 }}>{t}</div><div style={{ fontSize: 14, color: 'var(--soft)', marginTop: 4, lineHeight: 1.5 }}>{x}</div></div>
        ))}
      </aside>
    </Shell>
  );
}
