# Intellectual property notes

DndSheet is an unofficial fan tool. It is not affiliated with, endorsed or sponsored by Wizards of the Coast. *Dungeons & Dragons* and *D&D* are trademarks of Wizards of the Coast LLC.

## What the app contains

- **Mechanical facts from the System Reference Document 5.1 (SRD 5.1).** These are class names, hit dice, saving-throw proficiencies, spellcasting abilities, spell-slot progression tables, the standard skills and conditions, and species and alignment names (`src/DndSheet.Core/Content/SrdCatalog.cs`, `Domain/Enums.cs`).
  This work includes material taken from the System Reference Document 5.1 ("SRD 5.1") by Wizards of the Coast LLC, available at <https://dnd.wizards.com/resources/systems-reference-document>. The SRD 5.1 is licensed under the Creative Commons Attribution 4.0 International License, available at <https://creativecommons.org/licenses/by/4.0/legalcode>.
- **Rules computed in code:** modifiers, proficiency bonus, death saves, rests. The rules themselves (as opposed to their expression in text) are implemented from their mechanics, and no rules text is reproduced.

## What it deliberately does not contain

- **No rulebook text.** It has no spell, feat, class-feature or monster descriptions. Spell and feature description fields are empty until the player types in their own notes. The UI's short helper phrases are original wording.
- **No official artwork, logos, fonts or character-sheet PDF.** The sheet view is an original layout that follows the *organization* of the familiar 5e sheet (where boxes are and what they hold), drawn with standard WPF controls. It does not trace or embed the official PDF.

## If content is added later

- Content from SRD 5.1 or SRD 5.2 (both CC-BY-4.0) can be bundled with the attribution above (SRD 5.2 needs its own attribution line).
- Non-SRD content (most subclasses, feats, spells and species from the published books) must not be bundled. Let users import their own data instead.
- Keep bundled content in data files separate from code, so its licensing is clear and it can be swapped out.
