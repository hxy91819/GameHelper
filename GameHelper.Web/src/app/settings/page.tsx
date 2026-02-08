"use client";

import { useState, useEffect } from "react";
import useSWR from "swr";
import { Loader2, Save } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from "@/components/ui/card";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { toast } from "sonner";
import { fetcher, settingsApi, type SettingsDto } from "@/lib/api";

export default function SettingsPage() {
  const { data: settings, isLoading, mutate } = useSWR<SettingsDto>("/api/settings", fetcher);
  const [monitorType, setMonitorType] = useState("ETW");
  const [autoStart, setAutoStart] = useState(false);
  const [launchOnStartup, setLaunchOnStartup] = useState(false);
  const [saving, setSaving] = useState(false);
  const [dirty, setDirty] = useState(false);

  useEffect(() => {
    if (settings) {
      setMonitorType(settings.processMonitorType);
      setAutoStart(settings.autoStartInteractiveMonitor);
      setLaunchOnStartup(settings.launchOnSystemStartup);
      setDirty(false);
    }
  }, [settings]);

  const handleSave = async () => {
    setSaving(true);
    try {
      await settingsApi.update({
        processMonitorType: monitorType,
        autoStartInteractiveMonitor: autoStart,
        launchOnSystemStartup: launchOnStartup,
      });
      toast.success("Settings saved successfully");
      setDirty(false);
      mutate();
    } catch {
      toast.error("Failed to save settings");
    } finally {
      setSaving(false);
    }
  };

  if (isLoading) {
    return (
      <div className="flex h-64 items-center justify-center">
        <Loader2 className="h-8 w-8 animate-spin text-muted-foreground" />
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <h1 className="text-2xl font-bold">Settings</h1>
        <Button onClick={handleSave} disabled={saving || !dirty}>
          {saving ? (
            <Loader2 className="mr-2 h-4 w-4 animate-spin" />
          ) : (
            <Save className="mr-2 h-4 w-4" />
          )}
          Save Changes
        </Button>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Process Monitoring</CardTitle>
          <CardDescription>
            Configure how GameHelper detects running games.
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          <div className="flex items-center justify-between">
            <div className="space-y-0.5">
              <Label>Monitor Type</Label>
              <p className="text-sm text-muted-foreground">
                ETW is faster but requires admin. WMI works without admin.
              </p>
            </div>
            <Select
              value={monitorType}
              onValueChange={(v) => { setMonitorType(v); setDirty(true); }}
            >
              <SelectTrigger className="w-32">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="ETW">ETW</SelectItem>
                <SelectItem value="WMI">WMI</SelectItem>
              </SelectContent>
            </Select>
          </div>
          <div className="flex items-center justify-between">
            <div className="space-y-0.5">
              <Label>Auto-start Monitor</Label>
              <p className="text-sm text-muted-foreground">
                Automatically start process monitoring on launch.
              </p>
            </div>
            <Switch
              checked={autoStart}
              onCheckedChange={(v) => { setAutoStart(v); setDirty(true); }}
            />
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Startup Behavior</CardTitle>
          <CardDescription>
            Configure system startup preferences.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <div className="flex items-center justify-between">
            <div className="space-y-0.5">
              <Label>Launch on System Startup</Label>
              <p className="text-sm text-muted-foreground">
                Start GameHelper automatically when Windows starts.
              </p>
            </div>
            <Switch
              checked={launchOnStartup}
              onCheckedChange={(v) => { setLaunchOnStartup(v); setDirty(true); }}
            />
          </div>
        </CardContent>
      </Card>
    </div>
  );
}
