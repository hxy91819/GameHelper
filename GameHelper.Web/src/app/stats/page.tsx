"use client";

import useSWR from "swr";
import Link from "next/link";
import { Clock, Hash, Trophy, Loader2, BarChart3 } from "lucide-react";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import {
  AreaChart,
  Area,
  XAxis,
  YAxis,
  Tooltip,
  ResponsiveContainer,
  CartesianGrid,
} from "recharts";
import { fetcher, type GameStatsDto } from "@/lib/api";

function formatMinutes(m: number): string {
  if (m < 60) return `${m}m`;
  const h = Math.floor(m / 60);
  const rem = m % 60;
  return rem > 0 ? `${h}h ${rem}m` : `${h}h`;
}

function buildDailyTrend(stats: GameStatsDto[]): { date: string; minutes: number }[] {
  const days = 14;
  const now = new Date();
  const map = new Map<string, number>();

  for (let i = days - 1; i >= 0; i--) {
    const d = new Date(now);
    d.setDate(d.getDate() - i);
    map.set(d.toISOString().slice(0, 10), 0);
  }

  for (const game of stats) {
    for (const s of game.sessions) {
      const dateKey = new Date(s.endTime).toISOString().slice(0, 10);
      if (map.has(dateKey)) {
        map.set(dateKey, (map.get(dateKey) ?? 0) + s.durationMinutes);
      }
    }
  }

  return Array.from(map.entries()).map(([date, minutes]) => ({
    date: date.slice(5), // MM-DD
    minutes,
  }));
}

export default function StatsPage() {
  const { data: stats, isLoading } = useSWR<GameStatsDto[]>("/api/stats", fetcher);

  if (isLoading) {
    return (
      <div className="flex h-64 items-center justify-center">
        <Loader2 className="h-8 w-8 animate-spin text-muted-foreground" />
      </div>
    );
  }

  const allStats = stats ?? [];
  const totalMinutes = allStats.reduce((s, g) => s + g.totalMinutes, 0);
  const totalSessions = allStats.reduce((s, g) => s + g.sessionCount, 0);
  const mostPlayed = allStats.length > 0
    ? allStats.reduce((a, b) => (a.totalMinutes > b.totalMinutes ? a : b))
    : null;
  const maxMinutes = allStats.length > 0 ? Math.max(...allStats.map((g) => g.totalMinutes)) : 1;

  const trendData = buildDailyTrend(allStats);
  const ranked = [...allStats].sort((a, b) => b.totalMinutes - a.totalMinutes);

  if (allStats.length === 0) {
    return (
      <div className="space-y-6">
        <h1 className="text-2xl font-bold">Statistics</h1>
        <div className="flex flex-col items-center justify-center py-16">
          <BarChart3 className="h-12 w-12 text-muted-foreground mb-4" />
          <p className="text-lg font-medium">No playtime data</p>
          <p className="text-sm text-muted-foreground">
            Start monitoring games to collect statistics.
          </p>
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <h1 className="text-2xl font-bold">Statistics</h1>

      {/* Stat cards */}
      <div className="grid gap-4 sm:grid-cols-3">
        <Card>
          <CardHeader className="flex flex-row items-center justify-between pb-2">
            <CardTitle className="text-sm font-medium text-muted-foreground">
              Total Playtime
            </CardTitle>
            <Clock className="h-4 w-4 text-muted-foreground" />
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold">{formatMinutes(totalMinutes)}</div>
          </CardContent>
        </Card>
        <Card>
          <CardHeader className="flex flex-row items-center justify-between pb-2">
            <CardTitle className="text-sm font-medium text-muted-foreground">
              Total Sessions
            </CardTitle>
            <Hash className="h-4 w-4 text-muted-foreground" />
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold">{totalSessions}</div>
          </CardContent>
        </Card>
        <Card>
          <CardHeader className="flex flex-row items-center justify-between pb-2">
            <CardTitle className="text-sm font-medium text-muted-foreground">
              Most Played
            </CardTitle>
            <Trophy className="h-4 w-4 text-muted-foreground" />
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold truncate">
              {mostPlayed?.displayName || mostPlayed?.gameName || "-"}
            </div>
          </CardContent>
        </Card>
      </div>

      {/* Daily trend chart */}
      <Card>
        <CardHeader>
          <CardTitle className="text-base">Daily Playtime (Last 14 Days)</CardTitle>
        </CardHeader>
        <CardContent>
          <div className="h-64">
            <ResponsiveContainer width="100%" height="100%">
              <AreaChart data={trendData}>
                <CartesianGrid strokeDasharray="3 3" className="stroke-border" />
                <XAxis dataKey="date" className="text-xs" />
                <YAxis tickFormatter={(v) => formatMinutes(v)} className="text-xs" />
                <Tooltip
                  formatter={(value) => [formatMinutes(Number(value ?? 0)), "Playtime"]}
                  contentStyle={{
                    backgroundColor: "hsl(var(--card))",
                    border: "1px solid hsl(var(--border))",
                    borderRadius: "var(--radius)",
                  }}
                />
                <Area
                  type="monotone"
                  dataKey="minutes"
                  stroke="hsl(var(--chart-1))"
                  fill="hsl(var(--chart-1))"
                  fillOpacity={0.2}
                />
              </AreaChart>
            </ResponsiveContainer>
          </div>
        </CardContent>
      </Card>

      {/* Game ranking */}
      <Card>
        <CardHeader>
          <CardTitle className="text-base">Game Ranking</CardTitle>
        </CardHeader>
        <CardContent className="space-y-3">
          {ranked.map((game, i) => (
            <Link
              key={game.gameName}
              href={`/stats/detail?game=${encodeURIComponent(game.gameName)}`}
              className="flex items-center gap-3 rounded-lg px-3 py-2 transition-colors hover:bg-accent"
            >
              <span className="w-6 text-center text-sm font-bold text-muted-foreground">
                {i + 1}
              </span>
              <div className="flex-1 min-w-0">
                <p className="text-sm font-medium truncate">
                  {game.displayName || game.gameName}
                </p>
                <div className="mt-1 h-2 w-full rounded-full bg-muted">
                  <div
                    className="h-2 rounded-full bg-chart-1"
                    style={{ width: `${(game.totalMinutes / maxMinutes) * 100}%` }}
                  />
                </div>
              </div>
              <span className="text-sm text-muted-foreground whitespace-nowrap">
                {formatMinutes(game.totalMinutes)}
              </span>
            </Link>
          ))}
        </CardContent>
      </Card>
    </div>
  );
}
