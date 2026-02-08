"use client";

import useSWR from "swr";
import { Gamepad2, Clock, CalendarDays, Loader2 } from "lucide-react";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import {
  BarChart,
  Bar,
  XAxis,
  YAxis,
  Tooltip,
  ResponsiveContainer,
  CartesianGrid,
} from "recharts";
import { fetcher, getApiBase, type GameStatsDto, type GameDto } from "@/lib/api";

function formatMinutes(m: number): string {
  if (m < 60) return `${m}m`;
  const h = Math.floor(m / 60);
  const rem = m % h === 0 ? 0 : m % 60;
  return rem > 0 ? `${h}h ${rem}m` : `${h}h`;
}

export default function DashboardPage() {
  const { data: stats, isLoading: statsLoading, error: statsError } = useSWR<GameStatsDto[]>(
    "/api/stats",
    fetcher
  );
  const { data: games, isLoading: gamesLoading, error: gamesError } = useSWR<GameDto[]>(
    "/api/games",
    fetcher
  );

  const isLoading = statsLoading || gamesLoading;
  const error = statsError || gamesError;

  const totalMinutes = stats?.reduce((s, g) => s + g.totalMinutes, 0) ?? 0;
  const recentMinutes = stats?.reduce((s, g) => s + g.recentMinutes, 0) ?? 0;
  const monitoredCount = games?.length ?? 0;

  const chartData = (stats ?? [])
    .filter((g) => g.recentMinutes > 0)
    .sort((a, b) => b.recentMinutes - a.recentMinutes)
    .slice(0, 8)
    .map((g) => ({
      name: g.displayName || g.gameName,
      minutes: g.recentMinutes,
    }));

  if (isLoading) {
    return (
      <div className="flex h-64 items-center justify-center">
        <Loader2 className="h-8 w-8 animate-spin text-muted-foreground" />
      </div>
    );
  }

  if (error) {
    return (
      <div className="space-y-6">
        <h1 className="text-2xl font-bold">Dashboard</h1>
        <Card className="border-destructive">
          <CardContent className="flex flex-col items-center justify-center py-12">
            <p className="text-lg font-medium text-destructive mb-2">Unable to connect to API</p>
            <p className="text-sm text-muted-foreground mb-1">
              API endpoint: <code className="bg-muted px-1 rounded">{getApiBase()}</code>
            </p>
            <p className="text-sm text-muted-foreground mb-4">
              {error.message}
            </p>
            <p className="text-xs text-muted-foreground">
              Make sure the backend is running with <code className="bg-muted px-1 rounded">--web</code> flag.
            </p>
          </CardContent>
        </Card>
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <h1 className="text-2xl font-bold">Dashboard</h1>

      {/* Stat cards */}
      <div className="grid gap-4 sm:grid-cols-3">
        <Card>
          <CardHeader className="flex flex-row items-center justify-between pb-2">
            <CardTitle className="text-sm font-medium text-muted-foreground">
              Monitored Games
            </CardTitle>
            <Gamepad2 className="h-4 w-4 text-muted-foreground" />
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold">{monitoredCount}</div>
          </CardContent>
        </Card>
        <Card>
          <CardHeader className="flex flex-row items-center justify-between pb-2">
            <CardTitle className="text-sm font-medium text-muted-foreground">
              Total Playtime
            </CardTitle>
            <Clock className="h-4 w-4 text-muted-foreground" />
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold">
              {formatMinutes(totalMinutes)}
            </div>
          </CardContent>
        </Card>
        <Card>
          <CardHeader className="flex flex-row items-center justify-between pb-2">
            <CardTitle className="text-sm font-medium text-muted-foreground">
              Last 2 Weeks
            </CardTitle>
            <CalendarDays className="h-4 w-4 text-muted-foreground" />
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold">
              {formatMinutes(recentMinutes)}
            </div>
          </CardContent>
        </Card>
      </div>

      {/* Recent playtime chart */}
      {chartData.length > 0 ? (
        <Card>
          <CardHeader>
            <CardTitle className="text-base">
              Recent Playtime (Last 14 Days)
            </CardTitle>
          </CardHeader>
          <CardContent>
            <div className="h-72">
              <ResponsiveContainer width="100%" height="100%">
                <BarChart data={chartData} layout="vertical">
                  <CartesianGrid
                    strokeDasharray="3 3"
                    className="stroke-border"
                  />
                  <XAxis
                    type="number"
                    tickFormatter={(v) => formatMinutes(v)}
                    className="text-xs"
                  />
                  <YAxis
                    dataKey="name"
                    type="category"
                    width={120}
                    className="text-xs"
                    tick={{ fill: "hsl(var(--muted-foreground))" }}
                  />
                  <Tooltip
                    formatter={(value) => [
                      formatMinutes(Number(value ?? 0)),
                      "Playtime",
                    ]}
                    contentStyle={{
                      backgroundColor: "hsl(var(--card))",
                      border: "1px solid hsl(var(--border))",
                      borderRadius: "var(--radius)",
                    }}
                  />
                  <Bar
                    dataKey="minutes"
                    fill="hsl(var(--chart-1))"
                    radius={[0, 4, 4, 0]}
                  />
                </BarChart>
              </ResponsiveContainer>
            </div>
          </CardContent>
        </Card>
      ) : (
        <Card>
          <CardContent className="flex flex-col items-center justify-center py-16">
            <Clock className="h-12 w-12 text-muted-foreground mb-4" />
            <p className="text-lg font-medium">No recent playtime</p>
            <p className="text-sm text-muted-foreground">
              Start monitoring games to see your playtime here.
            </p>
          </CardContent>
        </Card>
      )}
    </div>
  );
}
