# TranslationExtractor

Extrait les textes à traduire d'un mod RimWorld (defs XML **+ patchs** + Keyed) vers
`out\<packageId>\Languages\French\{DefInjected,Keyed}`. Chaque entrée est précédée de commentaires :

```xml
<!-- EN: Exosuit move speed -->
<!-- Contexte: StatDef : nom de statistique affiché à gauche de la valeur ... rester COURT -->
<!-- Limite: ~22 car. (EN: 18) max=22 -->
<ExosuitMoveSpeed.label>TODO</ExosuitMoveSpeed.label>
```

## Utilisation
```
extract.cmd aoba.exosuit.framework --scan-dll
extract.cmd "C:\...\workshop\content\294100\3352894993"
extract.cmd --list-mods exo
extract.cmd --check out\aoba.exosuit.framework\Languages\French
extract.cmd --calibrate Core        (compare à la traduction FR officielle)
```
Options : `--version 1.6`, `--with id1,id2` (mods optionnels actifs), `--out`, `--placeholder`, `--prefill-en`,
`--fresh` (sans fusion), `--strict` (champs [MustTranslate] seulement), `--scan-dll` (où sont utilisées les clés Keyed).
`--skip-shipped` (ne pas extraire les entrées que le mod traduit déjà lui-même), `--no-implicit`, `--respect-conditions`, `--dump-def <defName>` (affiche le XML fusionné).

## Comportement
- Relancer fusionne : les traductions déjà saisies sont conservées, les EN modifiés sont signalés
  (`ATTENTION`), les clés disparues passent en commentaire `OBSOLETE`.
- Textes ajoutés/modifiés par les patchs du mod sur d'autres mods/Core : fichiers `Patches_<fichier>.xml`.
- `context-rules.json` : règles de contexte/longueur éditables (premier match gagne). Une règle avec `"soft": true` (noms d'objets : `ThingDef.label`, `labelPlural` ; noms de sols : `TerrainDef.label`) produit une « Limite souple » : `--check` signale un dépassement en `LONG (souple)` sans le compter comme erreur.
  Les limites sont des repères : une traduction naturelle et fidèle au vocabulaire officiel du jeu peut dépasser de quelques caractères (`TROP LONG` accepté, ex. VRE Genie). Raccourcir seulement les vrais débordements.
- `report.txt` : comptes, patchs C# non exécutés, types introuvables, champs texte non marqués ignorés.
  Ces champs ignorés sont souvent des références (ex. `FactionDef.dialogFactionGreeting*` contient le defName d'un `RulePackDef`, traduit avec celui-ci) : vérifier dans le XML avant de parler de texte manquant.
- **Toutes les conditions supposées remplies** (défaut) : deux passes, d'abord le mod seul (conditions respectées), puis avec tout actif (dossiers `IfModActive`, `MayRequire`, `FindMod`, CE…) ; l'union est extraite, la première passe l'emportant en cas de conflit (des variantes exclusives se remplacent l'une l'autre). `--respect-conditions` revient à une seule passe. Les patchs C# (ex. CE `MakeGunCECompatible`) restent non exécutés : leurs textes (outils `muzzle`/`head`, `Verb_ShootCE`…) ne peuvent pas être extraits.
- **Textes absents du XML** (désactivables par `--no-implicit`) : défauts fixés par le constructeur (`leaderTitle = "leader"`, lus dans l'IL, champs `[MustTranslate]` seulement) et formes déduites d'un autre champ, listées dans `implicit-rules.json` (`titleFemale`, `labelPlural`/`labelMale`/`labelFemale` des PawnKindDef et de leurs `lifeStages`, `labelNoun`, `stuffAdjective`, libellé du verbe unique d'une arme = nom de l'objet…). Ces entrées portent « Texte absent du XML : … » ; laissées à `TODO`, elles ne sont pas copiées dans un pack.
- Héritage XML : les `<li>` d'un enfant s'ajoutent à ceux du parent (comme `XmlInheritance`), sauf `Inherit="False"`.

- Si le mod embarque déjà du français (`Languages\French*`), l'outil l'annonce en console et dans `report.txt` (section « traduction déjà fournie par le mod ») : couverture, entrées manquantes, clés obsolètes, anciennes clés indexées (`comps.0.tools.0`). la traduction du mod n'est jamais modifiée.

## limites connues
- Patchs `PatchOperation` en C# (classe personnalisée) non exécutés : listés dans le rapport.
- Textes générés par le code du jeu non extraits : recettes/terrains implicites (Make_*, roche brute),
  valeurs par défaut calculées dans du code (non littérales), `debugLabel`. Le jeu génère ces recettes avec le Keyed de la langue.
- Contexte d'affichage heuristique (règles + analyse des chaînes des DLL).

## Repetitive translation helpers (post-extraction)

These commands only read and write the language files under `out\` (or a folder given as path); they never touch the game, the workshop or a mod. They fill **only** entries still at `TODO`, keep every comment, and put an `<!-- Auto (...) : à relire -->` comment above each value they write, so generated text can be reviewed (re-running the extraction drops that comment but keeps the value).

### Translation memory: `--fill-tm`
```
extract.cmd --fill-tm ks.flooringfloors            (same mod only)
extract.cmd --fill-tm lucius.floorpatch --tm-global (then every mod under out\)
```
For each `TODO`, copies the translation of another entry whose `EN:` comment is exactly the same text: first from the same mod, then (with `--tm-global`) from any mod under `out\`. Entries flagged `ATTENTION` (English changed since translation) are not used as a source. When the same English has several translations, the most frequent one is taken and the case is listed ("à harmoniser"). The report gives the number of filled entries and what is left: remaining `TODO`s, number of **distinct** English texts, and the most repeated ones. Workflow: translate one occurrence of each text, then run `--fill-tm` again. List entries (`<li>`) are not handled.

### Label generator for "material x pattern" mods: `--label-tables`, `--fill-labels`
Made for Flooring Floors and its patch mods, where labels combine a material and a pattern ("sapphire crosshatch M tile", "V cocoawood strip floor").
```
extract.cmd --label-tables ks.flooringfloors,lucius.floorpatch
   (edit out\ks.flooringfloors\label-patterns.csv and label-materials.csv)
extract.cmd --fill-labels ks.flooringfloors,lucius.floorpatch
extract.cmd --fill-labels ks.flooringfloors,lucius.floorpatch --relabel   (after correcting the tables)
```
- `--label-tables` splits every `.label` of the `DefInjected\TerrainDef` files (option `--label-folder`) into a pattern template and a material, then writes or **updates** two tables (default: in `out\<first id>\`, option `--tables <folder>`). Existing French values are kept, counts are refreshed.
  - `label-patterns.csv`: `en;fr;count;example`, e.g. `{M} crosshatch tile;carrelage en croisillon de {M};94;…`
  - `label-materials.csv`: `en;fr;elision;count;example;suggestion`. `suggestion` = French already given to the same English label somewhere under `out\` (our translations of the source mods), to copy into `fr` after checking.
  - CSV: `;` separator, UTF-8 with BOM (opens in Excel), `"..."` quoting.
- The split is inferred from the labels, not from defNames (`KS_StoneGravelKObsidian` is "schorl gravel paving"): each 1-3 word span is tried as material; the split whose template is shared by the most materials and whose material appears in the most templates wins, then a few rounds re-split each label after the majority (irregular families such as "stainless steel arx tile"). Splits used by fewer than `--min-share` (3) labels are dropped: those labels (typos, labels with no material) stay for the TM or manual translation.
- `--fill-labels` writes `fr` of the pattern with `{M}` replaced by `fr` of the material, for `TODO` labels whose pattern **and** material are translated. Write the pattern so that no agreement is needed ("carrelage en croisillon de {M}" → "carrelage en croisillon de saphir"). `de` + vowel becomes `d'` ("d'émeraude"); column `elision` forces it (`oui`, e.g. h muet: "d'hématite") or prevents it (`non`). `--relabel` also regenerates labels still carrying the generator's review comment.
- The report lists the untranslated patterns and materials that block the most labels.
- Then check with `--check`. `TerrainDef.label` has a soft limit (like `ThingDef.label`): the French "… de <material>" form is usually longer than the English, so overruns show as `LONG (souple)`, not as errors.
