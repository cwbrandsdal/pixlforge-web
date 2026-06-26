import { useEffect, useState } from "react";
import { AuthKitProvider, useAuth } from "@workos-inc/authkit-react";
import { ArrowRight, CheckCircle2, ImagePlus, LogOut, ShieldCheck, Sparkles } from "lucide-react";
import "./App.css";

type AuthConfig = {
  configured: boolean;
  clientId: string;
  apiHostname: string;
};

type ApiProfile = {
  subject: string | null;
  email: string | null;
  organizationId: string | null;
  role: string | null;
  permissions: string[];
};

function App() {
  const [config, setConfig] = useState<AuthConfig | null>(null);
  const [error, setError] = useState("");

  useEffect(() => {
    fetch("/api/auth/config")
      .then((response) => {
        if (!response.ok) throw new Error("Could not load auth configuration.");
        return response.json() as Promise<AuthConfig>;
      })
      .then(setConfig)
      .catch((caught: unknown) => setError(caught instanceof Error ? caught.message : String(caught)));
  }, []);

  if (error) return <SetupMessage title="Configuration error" detail={error} />;
  if (!config) return <SetupMessage title="Loading PixlForge" detail="Preparing authentication." />;
  if (!config.configured) {
    return (
      <SetupMessage
        title="WorkOS setup required"
        detail="Set WORKOS_CLIENT_ID and WORKOS_API_KEY, then restart the API."
      />
    );
  }

  return (
    <AuthKitProvider clientId={config.clientId} apiHostname={config.apiHostname}>
      <PixlForgeApp />
    </AuthKitProvider>
  );
}

function PixlForgeApp() {
  const { isLoading, user, signIn, signUp, signOut, getAccessToken, role, organizationId } = useAuth();
  const [profile, setProfile] = useState<ApiProfile | null>(null);
  const [apiError, setApiError] = useState("");

  useEffect(() => {
    if (window.location.pathname === "/login") {
      void signIn();
    }
  }, [signIn]);

  useEffect(() => {
    if (!user) {
      setProfile(null);
      return;
    }

    let canceled = false;
    async function loadProfile() {
      try {
        const token = await getAccessToken();
        const response = await fetch("/api/me", {
          headers: { Authorization: `Bearer ${token}` }
        });
        if (!response.ok) throw new Error(`API profile request failed: ${response.status}`);
        const nextProfile = await response.json() as ApiProfile;
        if (!canceled) {
          setProfile(nextProfile);
          setApiError("");
        }
      } catch (caught) {
        if (!canceled) setApiError(caught instanceof Error ? caught.message : String(caught));
      }
    }

    void loadProfile();
    return () => {
      canceled = true;
    };
  }, [getAccessToken, user]);

  if (isLoading) return <SetupMessage title="Loading session" detail="Checking your WorkOS session." />;

  if (!user) {
    return (
      <main className="shell">
        <section className="hero">
          <div className="hero-copy">
            <div className="mark"><Sparkles size={22} /> PixlForge</div>
            <h1>PixlForge</h1>
            <p>Create, manage, and publish image-generation workflows from a secure web workspace.</p>
            <div className="actions">
              <button type="button" onClick={() => void signIn()}>
                Sign in <ArrowRight size={18} />
              </button>
              <button type="button" className="secondary" onClick={() => void signUp()}>
                Create account
              </button>
            </div>
          </div>
          <div className="preview-panel">
            <div className="panel-bar">
              <span />
              <span />
              <span />
            </div>
            <div className="prompt-card">
              <ImagePlus size={28} />
              <strong>Batch prompt workspace</strong>
              <p>Draft variations, attach references, then promote selected results into production assets.</p>
            </div>
          </div>
        </section>
      </main>
    );
  }

  return (
    <main className="workspace">
      <header className="topbar">
        <div>
          <span className="eyebrow">PixlForge</span>
          <h1>Workspace</h1>
        </div>
        <button type="button" className="ghost" onClick={() => signOut({ returnTo: window.location.origin })}>
          <LogOut size={17} /> Sign out
        </button>
      </header>

      <section className="dashboard">
        <article>
          <ShieldCheck size={24} />
          <h2>Authenticated</h2>
          <p>{user.email}</p>
        </article>
        <article>
          <CheckCircle2 size={24} />
          <h2>API verified</h2>
          <p>{apiError || profile?.subject || "Loading profile..."}</p>
        </article>
        <article>
          <Sparkles size={24} />
          <h2>Access</h2>
          <p>{profile?.role || role || organizationId || "Default workspace"}</p>
        </article>
      </section>
    </main>
  );
}

function SetupMessage({ title, detail }: { title: string; detail: string }) {
  return (
    <main className="setup">
      <div className="mark"><Sparkles size={22} /> PixlForge</div>
      <h1>{title}</h1>
      <p>{detail}</p>
    </main>
  );
}

export default App;
