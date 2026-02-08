"use client";

import { useState } from "react";
import useSWR from "swr";
import { Plus, Pencil, Trash2, Gamepad2, Search, Loader2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Switch } from "@/components/ui/switch";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { toast } from "sonner";
import { fetcher, gamesApi, type GameDto } from "@/lib/api";
import { AddGameDialog } from "./add-game-dialog";
import { EditGameDialog } from "./edit-game-dialog";
import { DeleteGameDialog } from "./delete-game-dialog";

export default function GamesPage() {
  const { data: games, isLoading, mutate } = useSWR<GameDto[]>("/api/games", fetcher);
  const [search, setSearch] = useState("");
  const [addOpen, setAddOpen] = useState(false);
  const [editGame, setEditGame] = useState<GameDto | null>(null);
  const [deleteGame, setDeleteGame] = useState<GameDto | null>(null);

  const filtered = (games ?? []).filter((g) => {
    const q = search.toLowerCase();
    return (
      (g.displayName?.toLowerCase().includes(q) ?? false) ||
      (g.executableName?.toLowerCase().includes(q) ?? false) ||
      g.dataKey.toLowerCase().includes(q)
    );
  });

  const handleToggleEnabled = async (game: GameDto) => {
    try {
      await gamesApi.update(game.dataKey, {
        executableName: game.executableName,
        executablePath: game.executablePath,
        displayName: game.displayName,
        isEnabled: !game.isEnabled,
        hdrEnabled: game.hdrEnabled,
      });
      mutate();
    } catch (e) {
      toast.error(`Failed to update game: ${e instanceof Error ? e.message : "unknown error"}`);
    }
  };

  const handleToggleHdr = async (game: GameDto) => {
    try {
      await gamesApi.update(game.dataKey, {
        executableName: game.executableName,
        executablePath: game.executablePath,
        displayName: game.displayName,
        isEnabled: game.isEnabled,
        hdrEnabled: !game.hdrEnabled,
      });
      mutate();
    } catch (e) {
      toast.error(`Failed to update game: ${e instanceof Error ? e.message : "unknown error"}`);
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
        <h1 className="text-2xl font-bold">Game Library</h1>
        <Button onClick={() => setAddOpen(true)}>
          <Plus className="mr-2 h-4 w-4" />
          Add Game
        </Button>
      </div>

      {(games?.length ?? 0) > 0 && (
        <div className="relative">
          <Search className="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
          <Input
            placeholder="Search games..."
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            className="pl-9"
          />
        </div>
      )}

      {filtered.length > 0 ? (
        <div className="rounded-md border">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Display Name</TableHead>
                <TableHead>Executable</TableHead>
                <TableHead className="w-24 text-center">Monitor</TableHead>
                <TableHead className="w-20 text-center">HDR</TableHead>
                <TableHead className="w-24 text-right">Actions</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {filtered.map((game) => (
                <TableRow key={game.dataKey}>
                  <TableCell className="font-medium">
                    {game.displayName || game.dataKey}
                  </TableCell>
                  <TableCell className="text-muted-foreground text-sm">
                    {game.executableName || "-"}
                  </TableCell>
                  <TableCell className="text-center">
                    <Switch
                      checked={game.isEnabled}
                      onCheckedChange={() => handleToggleEnabled(game)}
                    />
                  </TableCell>
                  <TableCell className="text-center">
                    <Switch
                      checked={game.hdrEnabled}
                      onCheckedChange={() => handleToggleHdr(game)}
                    />
                  </TableCell>
                  <TableCell className="text-right">
                    <div className="flex justify-end gap-1">
                      <Button
                        variant="ghost"
                        size="icon"
                        className="h-8 w-8"
                        onClick={() => setEditGame(game)}
                      >
                        <Pencil className="h-4 w-4" />
                      </Button>
                      <Button
                        variant="ghost"
                        size="icon"
                        className="h-8 w-8 text-destructive"
                        onClick={() => setDeleteGame(game)}
                      >
                        <Trash2 className="h-4 w-4" />
                      </Button>
                    </div>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      ) : (games?.length ?? 0) === 0 ? (
        <div className="flex flex-col items-center justify-center py-16">
          <Gamepad2 className="h-12 w-12 text-muted-foreground mb-4" />
          <p className="text-lg font-medium">No games configured</p>
          <p className="text-sm text-muted-foreground mb-4">
            Add your first game to get started with monitoring.
          </p>
          <Button onClick={() => setAddOpen(true)}>
            <Plus className="mr-2 h-4 w-4" />
            Add Game
          </Button>
        </div>
      ) : (
        <p className="text-center text-muted-foreground py-8">
          No games match your search.
        </p>
      )}

      <AddGameDialog open={addOpen} onOpenChange={setAddOpen} onSuccess={() => mutate()} />
      <EditGameDialog game={editGame} onOpenChange={(open: boolean) => !open && setEditGame(null)} onSuccess={() => mutate()} />
      <DeleteGameDialog game={deleteGame} onOpenChange={(open: boolean) => !open && setDeleteGame(null)} onSuccess={() => mutate()} />
    </div>
  );
}
