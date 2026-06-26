import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { AuthKitProvider, useAuth } from "@workos-inc/authkit-react";
import {
  AlertCircle,
  CheckCircle2,
  Image as ImageIcon,
  Images,
  KeyRound,
  LoaderCircle,
  LogOut,
  Maximize2,
  Plus,
  Save,
  Settings,
  Sparkles,
  Square,
  SquareCheck,
  Trash2,
  Upload,
  Wand2,
  X
} from "lucide-react";
import "./App.css";

type AuthConfig = { configured: boolean; clientId: string; apiHostname: string };
type SettingsState = {
  count: number;
  aspectRatio: string;
  openAiModel: string;
  openAiSize: string;
  openAiQuality: string;
  openAiFormat: string;
  openAiModeration: string;
};
type ReferenceFile = { id: string; name: string; assetId: string; contentType: string; size: number; addedAt: string };
type Project = { id: string; name: string; references: ReferenceFile[]; createdAt: string; updatedAt: string };
type Generation = {
  id: string;
  projectId: string;
  prompt: string;
  provider: string;
  kind: string;
  parentGenerationId: string;
  assetId: string;
  status: string;
  error: string;
  createdAt: string;
  model: string;
  size: string;
  batchId: string;
  index: number;
};
type AppState = { settings: SettingsState; projects: Project[]; activeProjectId: string; generations: Generation[] };
type SecretStatus = { openAiApiKeySaved: boolean; openAiApiKeyFromEnv: boolean };

const promptExamples = [
  "A premium product render of a compact AI image studio called PixlForge, precise lighting, crisp material detail, no UI mockup text",
  "Three editorial campaign visuals for a modern creative software brand, bold composition, clean color hierarchy, photorealistic finish",
  "A cinematic workspace where luminous image tiles are being forged into final artwork, high-end 3D render, green accent light"
];

function App() {
  const [config, setConfig] = useState<AuthConfig | null>(null);
  const [error, setError] = useState("");

  useEffect(() => {
    fetch("/api/auth/config")
      .then((response) => response.ok ? response.json() : Promise.reject(new Error("Could not load auth config.")))
      .then(setConfig)
      .catch((caught: unknown) => setError(caught instanceof Error ? caught.message : String(caught)));
  }, []);

  if (error) return <Setup title="Configuration error" detail={error} />;
  if (!config) return <Setup title="PixlForge" detail="Loading workspace." />;
  if (!config.configured) return <Setup title="WorkOS required" detail="Configure WorkOS before opening PixlForge." />;

  return (
    <AuthKitProvider clientId={config.clientId} apiHostname={config.apiHostname}>
      <Workspace />
    </AuthKitProvider>
  );
}

function Workspace() {
  const { isLoading, user, signIn, signUp, signOut, getAccessToken } = useAuth();
  const [state, setState] = useState<AppState | null>(null);
  const [secrets, setSecrets] = useState<SecretStatus>({ openAiApiKeySaved: false, openAiApiKeyFromEnv: false });
  const [prompt, setPrompt] = useState(promptExamples[0]);
  const [status, setStatus] = useState("Ready");
  const [apiKey, setApiKey] = useState("");
  const [newProjectName, setNewProjectName] = useState("");
  const [selectedIds, setSelectedIds] = useState<string[]>([]);
  const [busy, setBusy] = useState("");
  const fileInputRef = useRef<HTMLInputElement | null>(null);

  const api = useCallback(async <T,>(path: string, init: RequestInit = {}): Promise<T> => {
    const token = await getAccessToken();
    const headers = new Headers(init.headers);
    headers.set("Authorization", `Bearer ${token}`);
    if (init.body && !(init.body instanceof FormData)) headers.set("Content-Type", "application/json");
    const response = await fetch(path, { ...init, headers });
    if (!response.ok) {
      const text = await response.text();
      throw new Error(text || `Request failed: ${response.status}`);
    }
    return response.json() as Promise<T>;
  }, [getAccessToken]);

  const load = useCallback(async () => {
    const [nextState, nextSecrets] = await Promise.all([
      api<AppState>("/api/state"),
      api<SecretStatus>("/api/secrets/openai")
    ]);
    setState(nextState);
    setSecrets(nextSecrets);
  }, [api]);

  useEffect(() => {
    if (window.location.pathname === "/login") void signIn();
  }, [signIn]);

  useEffect(() => {
    if (!user) return;
    void load().catch((caught: unknown) => setStatus(caught instanceof Error ? caught.message : String(caught)));
  }, [load, user]);

  const activeProject = useMemo(() => state?.projects.find((project) => project.id === state.activeProjectId) ?? state?.projects[0] ?? null, [state]);
  const generations = useMemo(() => state?.generations.filter((generation) => generation.projectId === activeProject?.id) ?? [], [activeProject, state]);
  const drafts = generations.filter((generation) => generation.kind !== "final" && generation.status === "completed");
  const finals = generations.filter((generation) => generation.kind === "final" && generation.status === "completed");
  const selectedDrafts = drafts.filter((generation) => selectedIds.includes(generation.id));
  const keyReady = secrets.openAiApiKeyFromEnv || secrets.openAiApiKeySaved;

  if (isLoading) return <Setup title="PixlForge" detail="Checking your session." />;
  if (!user) {
    return (
      <main className="auth-screen">
        <section>
          <div className="brand-mark"><Sparkles size={22} /> PixlForge</div>
          <h1>PixlForge</h1>
          <p>Generate image batches, attach reference files, review drafts, and upscale selected results to 4K from the browser.</p>
          <div className="actions">
            <button type="button" onClick={() => void signIn()}><KeyRound size={18} /> Sign in</button>
            <button type="button" className="secondary" onClick={() => void signUp()}>Create account</button>
          </div>
        </section>
      </main>
    );
  }

  if (!state) return <Setup title="Loading workspace" detail={status} />;

  async function mutate<T>(label: string, action: () => Promise<T>, after?: (value: T) => void) {
    setBusy(label);
    setStatus(label);
    try {
      const result = await action();
      after?.(result);
      if (!after) await load();
      setStatus("Ready");
    } catch (caught) {
      setStatus(caught instanceof Error ? caught.message : String(caught));
    } finally {
      setBusy("");
    }
  }

  async function updateSettings(patch: Partial<SettingsState>) {
    if (!state) return;
    const next = { ...state.settings, ...patch };
    setState((current) => current ? { ...current, settings: next } : current);
    await mutate("Saving settings", () => api<SettingsState>("/api/settings", { method: "POST", body: JSON.stringify(next) }), (settings) => {
      setState((current) => current ? { ...current, settings } : current);
    });
  }

  const assetUrl = (assetId: string) => `/api/assets/${assetId}`;

  return (
    <main className="app">
      <header className="topbar">
        <div>
          <span className="eyebrow">PixlForge</span>
          <h1>Forge</h1>
          <p>{status}</p>
        </div>
        <button type="button" className="secondary" onClick={() => signOut({ returnTo: window.location.origin })}>
          <LogOut size={17} /> Sign out
        </button>
      </header>

      <section className="layout">
        <aside className="panel controls">
          <div className="section-title"><Wand2 size={18} /><h2>Prompt</h2></div>
          <textarea value={prompt} onChange={(event) => setPrompt(event.target.value)} />
          <div className="example-row">
            {promptExamples.map((example, index) => <button key={example} type="button" onClick={() => setPrompt(example)}>{index + 1}</button>)}
          </div>

          <div className="section-title"><Settings size={18} /><h2>Settings</h2></div>
          <div className="grid2">
            <label>Count<input type="number" min={1} max={10} value={state.settings.count} onChange={(event) => void updateSettings({ count: Number(event.target.value) })} /></label>
            <label>Shape<select value={state.settings.aspectRatio} onChange={(event) => void updateSettings({ aspectRatio: event.target.value })}>
              <option value="1:1">Square</option><option value="16:9">Wide</option><option value="9:16">Vertical</option><option value="4:3">Classic</option><option value="3:4">Portrait</option>
            </select></label>
          </div>
          <label>Model<input value={state.settings.openAiModel} onChange={(event) => void updateSettings({ openAiModel: event.target.value })} /></label>
          <div className="grid2">
            <label>Size<select value={state.settings.openAiSize} onChange={(event) => void updateSettings({ openAiSize: event.target.value })}>
              <option value="1024x1024">1024x1024</option><option value="1536x1024">1536x1024</option><option value="1024x1536">1024x1536</option><option value="auto">Auto</option>
            </select></label>
            <label>Quality<select value={state.settings.openAiQuality} onChange={(event) => void updateSettings({ openAiQuality: event.target.value })}>
              <option value="auto">Auto</option><option value="low">Low</option><option value="medium">Medium</option><option value="high">High</option>
            </select></label>
          </div>

          <div className="section-title"><KeyRound size={18} /><h2>OpenAI key</h2></div>
          <div className="key-row">
            <input type="password" value={apiKey} placeholder={keyReady ? "Saved" : "sk-..."} onChange={(event) => setApiKey(event.target.value)} />
            <button type="button" title="Save key" onClick={() => void mutate("Saving key", () => api<SecretStatus>("/api/secrets/openai", { method: "POST", body: JSON.stringify({ apiKey }) }), (next) => { setSecrets(next); setApiKey(""); })}><Save size={16} /></button>
            <button type="button" title="Clear key" onClick={() => void mutate("Clearing key", () => api<SecretStatus>("/api/secrets/openai", { method: "DELETE" }), setSecrets)}><X size={16} /></button>
          </div>
          <p className={keyReady ? "ok" : "warn"}>{keyReady ? "OpenAI API key ready" : "Add your OpenAI API key before generating."}</p>

          <button
            type="button"
            className="generate"
            disabled={Boolean(busy) || !activeProject || !keyReady}
            onClick={() => void mutate("Generating images", () => api<{ generations: Generation[] }>("/api/generate", { method: "POST", body: JSON.stringify({ projectId: activeProject?.id, prompt, settings: state.settings }) }))}
          >
            {busy === "Generating images" ? <LoaderCircle className="spin" size={20} /> : <Sparkles size={20} />} Create drafts
          </button>
        </aside>

        <section className="maincol">
          <div className="panel projectbar">
            <label>Project<select value={activeProject?.id ?? ""} onChange={(event) => void mutate("Switching project", () => api<AppState>(`/api/projects/${event.target.value}/activate`, { method: "POST" }), setState)}>
              {state.projects.map((project) => <option key={project.id} value={project.id}>{project.name}</option>)}
            </select></label>
            <div className="new-project">
              <input value={newProjectName} placeholder="New project" onChange={(event) => setNewProjectName(event.target.value)} />
              <button type="button" title="Create project" onClick={() => void mutate("Creating project", () => api<AppState>("/api/projects", { method: "POST", body: JSON.stringify({ name: newProjectName }) }), (next) => { setState(next); setNewProjectName(""); })}><Plus size={16} /></button>
              <button type="button" title="Delete project" disabled={state.projects.length <= 1} onClick={() => void mutate("Deleting project", () => api<AppState>(`/api/projects/${activeProject?.id}`, { method: "DELETE" }), setState)}><Trash2 size={16} /></button>
            </div>
          </div>

          <div className="metrics">
            <Metric icon={<Images size={18} />} label="Drafts" value={String(drafts.length)} />
            <Metric icon={<Maximize2 size={18} />} label="4K finals" value={String(finals.length)} />
            <Metric icon={keyReady ? <CheckCircle2 size={18} /> : <AlertCircle size={18} />} label="OpenAI" value={keyReady ? "Ready" : "Missing"} />
          </div>

          <div className="panel references">
            <div className="section-title"><Upload size={18} /><h2>References</h2></div>
            <input ref={fileInputRef} hidden multiple type="file" onChange={(event) => {
              const files = event.target.files;
              if (!files || !activeProject) return;
              const form = new FormData();
              Array.from(files).forEach((file) => form.append("files", file));
              void mutate("Uploading references", () => api<AppState>(`/api/references/${activeProject.id}`, { method: "POST", body: form }), setState);
              event.target.value = "";
            }} />
            <button type="button" className="secondary" onClick={() => fileInputRef.current?.click()}><Upload size={16} /> Add files</button>
            <div className="refs">
              {activeProject?.references.map((reference) => (
                <div className="ref" key={reference.id}>
                  {reference.contentType.startsWith("image/") ? <img src={assetUrl(reference.assetId)} alt="" /> : <ImageIcon size={18} />}
                  <span>{reference.name}</span>
                  <button type="button" onClick={() => void mutate("Removing reference", () => api<AppState>(`/api/references/${activeProject.id}/${reference.id}`, { method: "DELETE" }), setState)}><X size={14} /></button>
                </div>
              ))}
            </div>
          </div>

          <div className="toolbar">
            <strong>{selectedDrafts.length} selected</strong>
            <button type="button" className="secondary" onClick={() => setSelectedIds(drafts.map((generation) => generation.id))}><SquareCheck size={16} /> Select drafts</button>
            <button type="button" className="secondary" onClick={() => setSelectedIds([])}><Square size={16} /> Clear</button>
            <button type="button" disabled={!selectedDrafts.length || Boolean(busy)} onClick={() => void mutate("Upscaling", () => api<{ generations: Generation[] }>("/api/upscale", { method: "POST", body: JSON.stringify({ projectId: activeProject?.id, generationIds: selectedIds }) }))}>
              {busy === "Upscaling" ? <LoaderCircle className="spin" size={16} /> : <Maximize2 size={16} />} 4K upscale
            </button>
          </div>

          <div className="gallery">
            {generations.length ? generations.map((generation) => (
              <article className="image-card" key={generation.id}>
                <button type="button" className="image-frame" onClick={() => window.open(assetUrl(generation.assetId), "_blank", "noopener,noreferrer")}>
                  <img src={assetUrl(generation.assetId)} alt="" />
                </button>
                <div className="card-meta">
                  <button type="button" className={selectedIds.includes(generation.id) ? "selected" : ""} disabled={generation.kind === "final"} onClick={() => setSelectedIds((current) => current.includes(generation.id) ? current.filter((id) => id !== generation.id) : [...current, generation.id])}>
                    {selectedIds.includes(generation.id) ? <SquareCheck size={16} /> : <Square size={16} />}
                  </button>
                  <div><strong>{generation.kind === "final" ? "4K Final" : "Draft"} #{generation.index}</strong><span>{generation.size || generation.model}</span></div>
                  <button type="button" onClick={() => void mutate("Deleting image", () => api<AppState>(`/api/generations/${generation.id}`, { method: "DELETE" }), setState)}><Trash2 size={16} /></button>
                </div>
              </article>
            )) : <div className="empty"><ImageIcon size={42} /><strong>No images yet</strong><span>Create drafts to fill this workspace.</span></div>}
          </div>
        </section>
      </section>
    </main>
  );
}

function Metric({ icon, label, value }: { icon: React.ReactNode; label: string; value: string }) {
  return <div className="metric">{icon}<span>{label}</span><strong>{value}</strong></div>;
}

function Setup({ title, detail }: { title: string; detail: string }) {
  return <main className="setup"><div className="brand-mark"><Sparkles size={22} /> PixlForge</div><h1>{title}</h1><p>{detail}</p></main>;
}

export default App;
