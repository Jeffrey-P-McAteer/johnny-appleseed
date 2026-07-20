namespace JohnnyAppleseed.Story;

/// <summary>
/// One screen of the intro narration.
/// </summary>
/// <param name="Heading">
/// Short label shown above the body — a place and/or year, e.g. "Ohio Country · 1801".
/// May be empty for a headingless page.
/// </param>
/// <param name="Body">
/// The narration paragraph, revealed with the typewriter effect. Plain text;
/// the dialogue box word-wraps it automatically. Use a blank line ("\n\n") for a
/// deliberate paragraph break inside one page.
/// </param>
sealed record StoryPage(string Heading, string Body);

/// <summary>
/// The clickable introduction to the early 1800s.
///
/// ─────────────────────────────────────────────────────────────────────────────
///  ✍  FOR THE WRITER
///  These pages are PLACEHOLDER TEMPLATE COPY. Replace the Heading/Body strings
///  below with the final prose. The structure is deliberately simple so you can
///  edit text without touching any game code:
///
///     • Add a page      →  add another `new StoryPage("Heading", "Body")` entry.
///     • Remove a page   →  delete its entry.
///     • Reorder         →  move entries around.
///     • No heading      →  pass "" as the Heading.
///     • Paragraph break →  put "\n\n" inside the Body.
///
///  Page count and order can change freely; a saved game stores the player's
///  page index and is clamped to the current length on load, so shortening the
///  script will never strand a returning player past the end.
/// ─────────────────────────────────────────────────────────────────────────────
/// </summary>
static class IntroScript
{
    public static readonly StoryPage[] Pages =
    [
        new StoryPage(
            "The American Frontier · 1801",
            "[[ TEMPLATE — opening beat. Set the scene: a young republic, twenty-five "
          + "years old, its edges dissolving into unmapped forest. Establish the mood "
          + "of the era before we meet anyone. Rewrite this paragraph. ]]"),

        new StoryPage(
            "Westward",
            "[[ TEMPLATE — the frontier is moving. Settlers pushing into the Ohio "
          + "Country; wilderness giving way to the first cabins and cleared fields. "
          + "Hint at hardship and hope in equal measure. Rewrite this paragraph. ]]"),

        new StoryPage(
            "A Man With a Sack of Seeds",
            "[[ TEMPLATE — introduce John Chapman. An ordinary wanderer with an "
          + "extraordinary habit: he plants apple trees ahead of the settlers, so "
          + "that orchards are waiting when they arrive. Rewrite this paragraph. ]]"),

        new StoryPage(
            "",
            "[[ TEMPLATE — a quieter, headingless beat. Give a sense of his character: "
          + "barefoot, gentle, at home among strangers and animals alike. This page "
          + "has no heading on purpose. Rewrite this paragraph. ]]"),

        new StoryPage(
            "Your Journey Begins",
            "[[ TEMPLATE — the hand-off to the player. The trail leads on; there are "
          + "seeds to plant and miles to walk. Close the intro and invite the player "
          + "forward into the game. Rewrite this paragraph. ]]"),
    ];
}
