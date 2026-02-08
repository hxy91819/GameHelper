const API_BASE = process.env.NEXT_PUBLIC_API_URL || "http://localhost:5123";

// --- Types ---

export interface GameDto {
  dataKey: string;
  executableName: string | null;
  executablePath: string | null;
  displayName: string | null;
  isEnabled: boolean;
  hdrEnabled: boolean;
}

export interface CreateGameRequest {
  executableName: string;
  executablePath?: string;
  displayName?: string;
  isEnabled: boolean;
  hdrEnabled: boolean;
}

export interface UpdateGameRequest {
  executableName?: string | null;
  executablePath?: string | null;
  displayName?: string | null;
  isEnabled: boolean;
  hdrEnabled: boolean;
}

export interface SettingsDto {
  processMonitorType: string;
  autoStartInteractiveMonitor: boolean;
  launchOnSystemStartup: boolean;
}

export interface UpdateSettingsRequest {
  processMonitorType?: string;
  autoStartInteractiveMonitor: boolean;
  launchOnSystemStartup: boolean;
}

export interface SessionDto {
  startTime: string;
  endTime: string;
  durationMinutes: number;
}

export interface GameStatsDto {
  gameName: string;
  displayName: string | null;
  totalMinutes: number;
  recentMinutes: number;
  sessionCount: number;
  sessions: SessionDto[];
}

// --- Fetch helpers ---

async function request<T>(path: string, options?: RequestInit): Promise<T> {
  const headers: Record<string, string> = {};
  if (options?.body) {
    headers["Content-Type"] = "application/json";
  }
  const res = await fetch(`${API_BASE}${path}`, {
    ...options,
    headers: { ...headers, ...options?.headers as Record<string, string> },
  });
  if (!res.ok) {
    const body = await res.text();
    throw new Error(`API ${res.status}: ${body}`);
  }
  if (res.status === 204) return undefined as T;
  return res.json();
}

export function getApiBase() {
  return API_BASE;
}

// --- Game API ---

export const gamesApi = {
  list: () => request<GameDto[]>("/api/games"),
  create: (data: CreateGameRequest) =>
    request<GameDto>("/api/games", {
      method: "POST",
      body: JSON.stringify(data),
    }),
  update: (dataKey: string, data: UpdateGameRequest) =>
    request<GameDto>(`/api/games/${encodeURIComponent(dataKey)}`, {
      method: "PUT",
      body: JSON.stringify(data),
    }),
  delete: (dataKey: string) =>
    request<void>(`/api/games/${encodeURIComponent(dataKey)}`, {
      method: "DELETE",
    }),
};

// --- Settings API ---

export const settingsApi = {
  get: () => request<SettingsDto>("/api/settings"),
  update: (data: UpdateSettingsRequest) =>
    request<SettingsDto>("/api/settings", {
      method: "PUT",
      body: JSON.stringify(data),
    }),
};

// --- Stats API ---

export const statsApi = {
  list: () => request<GameStatsDto[]>("/api/stats"),
  getByGame: (gameName: string) =>
    request<GameStatsDto>(`/api/stats/${encodeURIComponent(gameName)}`),
};

// --- SWR fetcher ---

export const fetcher = <T>(path: string): Promise<T> => request<T>(path);
