# Story & content — writer's guide

This folder holds everything a **writer** edits. You should never need to touch
C# code to add story, items, or characters. (This README is documentation only —
it is not shipped inside the game.)

```
story/
  README.md               ← you are here
  items/items.jsonc       ← inventory items (shared by story + future map)
  characters/characters.jsonc ← who can speak / appear
  chapters/…              ← the narrative itself (Ink .ink files) — arriving in Phase 1
```

## Comments

All the data files here are **JSONC**: normal JSON, but you may leave notes with

```jsonc
// a line comment
/* or a block comment */
```

and trailing commas are fine. The game ignores them. (When the Ink story files
land, Ink uses the same `//` and `/* */` comment style, so the convention is
consistent everywhere.)

## Items (`items/items.jsonc`)

Each item is defined **once** and reused by every view of it — the face-to-face
story scene now, and the RPG-style map/codex later. The story refers to an item
by its `id`.

| field | meaning |
|-------|---------|
| `id` | required, unique, `lowercase-with-dashes`. How story/code reference it. |
| `name` | display name |
| `description` | plain tooltip / codex text |
| `greentext` | optional green flavor/quote lines (the classic `>` style) |
| `icon` | texture key; drop the art in `graphics/ui/items/` |
| `stackable` | `true` if copies combine into one count (default `true`) |

## Characters (`characters/characters.jsonc`)

| field | meaning |
|-------|---------|
| `id` | required, unique |
| `name` | display name |
| `description` | shared biography / codex text (same text used on the map) |
| `portrait` | texture key; drop the art in `graphics/portraits/` |
| `textColor` | optional `#RRGGBB` tint for the speaker's name |

## Where art & music go

Assets are embedded by their path, so you reference them by that path (the
"key"):

```
graphics/scenes/<chapter>/<name>.jpg   backgrounds
graphics/portraits/<character>.png     character portraits
graphics/ui/items/<item>.png           item icons
audio/music/<name>.ogg                 looping background music
audio/ambience/<name>.ogg              ambient beds
audio/sfx/<name>.mp3                    short sound effects
```

Add the file, reference its key (e.g. `graphics/portraits/johnny.png`), rebuild.
It doesn't have to exist yet to define an item/character — a missing icon just
won't render until the art is added.
