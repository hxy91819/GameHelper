"use client";

import { useState } from "react";
import { Loader2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import {
  AlertDialog,
  AlertDialogContent,
  AlertDialogHeader,
  AlertDialogTitle,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogCancel,
} from "@/components/ui/alert-dialog";
import { toast } from "sonner";
import { gamesApi, type GameDto } from "@/lib/api";

interface Props {
  game: GameDto | null;
  onOpenChange: (open: boolean) => void;
  onSuccess: () => void;
}

export function DeleteGameDialog({ game, onOpenChange, onSuccess }: Props) {
  const [deleting, setDeleting] = useState(false);

  const handleDelete = async () => {
    if (!game) return;
    setDeleting(true);
    try {
      await gamesApi.delete(game.dataKey);
      toast.success("Game deleted successfully");
      onOpenChange(false);
      onSuccess();
    } catch {
      toast.error("Failed to delete game");
    } finally {
      setDeleting(false);
    }
  };

  return (
    <AlertDialog open={!!game} onOpenChange={onOpenChange}>
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>Delete Game</AlertDialogTitle>
          <AlertDialogDescription>
            Are you sure you want to delete{" "}
            <span className="font-semibold">
              {game?.displayName || game?.dataKey}
            </span>
            ? This action cannot be undone.
          </AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter>
          <AlertDialogCancel>Cancel</AlertDialogCancel>
          <Button variant="destructive" onClick={handleDelete} disabled={deleting}>
            {deleting && <Loader2 className="mr-2 h-4 w-4 animate-spin" />}
            Delete
          </Button>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}
