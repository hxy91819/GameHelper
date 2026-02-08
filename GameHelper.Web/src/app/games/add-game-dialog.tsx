"use client";

import { useState } from "react";
import { Loader2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogFooter,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import { toast } from "sonner";
import { gamesApi } from "@/lib/api";

interface Props {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onSuccess: () => void;
}

export function AddGameDialog({ open, onOpenChange, onSuccess }: Props) {
  const [executableName, setExecutableName] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [executablePath, setExecutablePath] = useState("");
  const [isEnabled, setIsEnabled] = useState(true);
  const [hdrEnabled, setHdrEnabled] = useState(false);
  const [saving, setSaving] = useState(false);

  const reset = () => {
    setExecutableName("");
    setDisplayName("");
    setExecutablePath("");
    setIsEnabled(true);
    setHdrEnabled(false);
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!executableName.trim()) {
      toast.error("Executable name is required");
      return;
    }
    setSaving(true);
    try {
      await gamesApi.create({
        executableName: executableName.trim(),
        displayName: displayName.trim() || undefined,
        executablePath: executablePath.trim() || undefined,
        isEnabled,
        hdrEnabled,
      });
      toast.success("Game added successfully");
      reset();
      onOpenChange(false);
      onSuccess();
    } catch {
      toast.error("Failed to add game");
    } finally {
      setSaving(false);
    }
  };

  return (
    <Dialog open={open} onOpenChange={(v) => { if (!v) reset(); onOpenChange(v); }}>
      <DialogContent className="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>Add Game</DialogTitle>
        </DialogHeader>
        <form onSubmit={handleSubmit} className="space-y-4">
          <div className="space-y-2">
            <Label htmlFor="execName">Executable Name *</Label>
            <Input
              id="execName"
              placeholder="e.g. game.exe"
              value={executableName}
              onChange={(e) => setExecutableName(e.target.value)}
              autoFocus
            />
          </div>
          <div className="space-y-2">
            <Label htmlFor="displayName">Display Name</Label>
            <Input
              id="displayName"
              placeholder="e.g. My Game"
              value={displayName}
              onChange={(e) => setDisplayName(e.target.value)}
            />
          </div>
          <div className="space-y-2">
            <Label htmlFor="execPath">Executable Path</Label>
            <Input
              id="execPath"
              placeholder="e.g. C:\Games\game.exe"
              value={executablePath}
              onChange={(e) => setExecutablePath(e.target.value)}
            />
          </div>
          <div className="flex items-center justify-between">
            <Label htmlFor="enabled">Enable Monitoring</Label>
            <Switch id="enabled" checked={isEnabled} onCheckedChange={setIsEnabled} />
          </div>
          <div className="flex items-center justify-between">
            <Label htmlFor="hdr">Enable HDR</Label>
            <Switch id="hdr" checked={hdrEnabled} onCheckedChange={setHdrEnabled} />
          </div>
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => { reset(); onOpenChange(false); }}>
              Cancel
            </Button>
            <Button type="submit" disabled={saving}>
              {saving && <Loader2 className="mr-2 h-4 w-4 animate-spin" />}
              Add
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
