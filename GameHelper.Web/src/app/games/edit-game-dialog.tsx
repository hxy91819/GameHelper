"use client";

import { useState, useEffect } from "react";
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
import { gamesApi, type GameDto } from "@/lib/api";

interface Props {
  game: GameDto | null;
  onOpenChange: (open: boolean) => void;
  onSuccess: () => void;
}

export function EditGameDialog({ game, onOpenChange, onSuccess }: Props) {
  const [executableName, setExecutableName] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [executablePath, setExecutablePath] = useState("");
  const [isEnabled, setIsEnabled] = useState(true);
  const [hdrEnabled, setHdrEnabled] = useState(false);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    if (game) {
      setExecutableName(game.executableName ?? "");
      setDisplayName(game.displayName ?? "");
      setExecutablePath(game.executablePath ?? "");
      setIsEnabled(game.isEnabled);
      setHdrEnabled(game.hdrEnabled);
    }
  }, [game]);

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!game) return;
    setSaving(true);
    try {
      await gamesApi.update(game.dataKey, {
        executableName: executableName.trim() || undefined,
        displayName: displayName.trim() || undefined,
        executablePath: executablePath.trim() || undefined,
        isEnabled,
        hdrEnabled,
      });
      toast.success("Game updated successfully");
      onOpenChange(false);
      onSuccess();
    } catch {
      toast.error("Failed to update game");
    } finally {
      setSaving(false);
    }
  };

  return (
    <Dialog open={!!game} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>Edit Game</DialogTitle>
        </DialogHeader>
        <form onSubmit={handleSubmit} className="space-y-4">
          <div className="space-y-2">
            <Label htmlFor="edit-execName">Executable Name</Label>
            <Input
              id="edit-execName"
              value={executableName}
              onChange={(e) => setExecutableName(e.target.value)}
            />
          </div>
          <div className="space-y-2">
            <Label htmlFor="edit-displayName">Display Name</Label>
            <Input
              id="edit-displayName"
              value={displayName}
              onChange={(e) => setDisplayName(e.target.value)}
            />
          </div>
          <div className="space-y-2">
            <Label htmlFor="edit-execPath">Executable Path</Label>
            <Input
              id="edit-execPath"
              value={executablePath}
              onChange={(e) => setExecutablePath(e.target.value)}
            />
          </div>
          <div className="flex items-center justify-between">
            <Label htmlFor="edit-enabled">Enable Monitoring</Label>
            <Switch id="edit-enabled" checked={isEnabled} onCheckedChange={setIsEnabled} />
          </div>
          <div className="flex items-center justify-between">
            <Label htmlFor="edit-hdr">Enable HDR</Label>
            <Switch id="edit-hdr" checked={hdrEnabled} onCheckedChange={setHdrEnabled} />
          </div>
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
              Cancel
            </Button>
            <Button type="submit" disabled={saving}>
              {saving && <Loader2 className="mr-2 h-4 w-4 animate-spin" />}
              Save
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
