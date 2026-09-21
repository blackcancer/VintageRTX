# VintageRTX — réécriture

Branche : `dev/renderer-rewrite-20260921`. Base historique conservée : `dev/renderer-recovery-20260918`, commit `1a82a0ca6f8a7566c19f56cfa7852312764db755`.

**État : R00, fondations exécutables et testables. Ce n'est pas encore le nouveau renderer en jeu.** Le module client observe les émetteurs et prépare leur état de trame ; il laisse volontairement le rendu natif intact. Il n'installe aucun hook, shader ni effet de l'ancien moteur. Ne pas présenter ce build comme une version photoréaliste installable.

## Objectif

Lumières et réflexions physiquement cohérentes, réactives, aussi naturelles que possible, sans dénaturer les modèles de Vintage Story. Scènes intérieures/extérieures, ombres des géométries fines et animées, reflets hors écran, métaux rugueux, eau et verre. Les optimisations ne doivent pas faire disparaître les sources ni imposer un délai global de stabilisation.

## Ce qui existe dans ce premier lot

- Nouveau cœur indépendant du jeu et d'OpenGL : états lumineux immuables par trame, identités structurelles, versions d'émission séparées de la géométrie.
- Profils temporels déterministes : feu/torche, lanterne protégée, lampe à huile, source stable, source pilotée par son état moteur et flash de foudre fini. La foudre n'est pas une lumière continue ni une flamme.
- Transactions de régions : construction, données CPU disponibles, publication GPU acquittée. Rejet des anciennes versions après édition, éviction ou changement de monde. Une région prête n'attend pas les autres.
- Ordonnanceur incrémental prioritaire avec coalescence, limites de travail et progression réservée aux tâches de fond.
- Intersections sur triangles et BVH CPU, coordonnées monde en double précision ; référence de transport diffus et conducteur GGX sur des scènes fournies complètes. Mêmes lumières pour les impacts primaires et secondaires, pas de plafond de huit sources.
- Adaptateur d'observation via API publique, sans ajouter de lumières natives en double. Commande client `.vrtxrewrite` : état de la fondation.

Le tracer CPU est un **instrument de référence**, pas un renderer temps réel du monde. Il ne gère pas encore les émetteurs surfaciques, la transparence, les textures ou les milieux imbriqués. Les traces de la scène complète fournie ne connaissent pas les chunks non chargés : la future traversal GPU doit consulter les états régionaux et retourner `Unknown`, jamais les confondre avec un ciel dégagé.

## Compilation

SDK .NET 10.0.401 ; solution Visual Studio : `VintageRTX.slnx`.

```powershell
dotnet test .\tests\VintageRTX.Core.Tests\VintageRTX.Core.Tests.csproj -c Release
$env:VINTAGE_STORY = 'C:\Chemin\Vers\Vintagestory'
dotnet build .\src\VintageRTX.Client\VintageRTX.Client.csproj -c Release
```

Le cœur et ses tests n'ont pas besoin d'une installation du jeu. L'adaptateur cible le client **1.22.7**. Seule référence supplémentaire activée dans ce premier adaptateur : **Newtonsoft.Json** fourni par le jeu (non copié). Ni Harmony, ni OpenTK, ni Skia ne sont activés à ce stade.

**Attention aux changements de branche :** utiliser de préférence un `git worktree` distinct. Ne pas déployer un ancien `bin/Debug/Mods/vintagertx` laissé par la branche recovery. Le nouveau projet a son propre chemin `src/VintageRTX.Client/bin/...` et n'importe aucun ancien asset automatiquement.

## Suite du développement et conditions de réception

Voir [ARCHITECTURE.md](docs/ARCHITECTURE.md) et [LIGHTING.md](docs/LIGHTING.md). R01 doit acquérir la vraie géométrie/matière du jeu et produire les premières passes GPU réactives. Les anciennes captures de référence restent dans l'historique ; les anciens succès CI ne sont pas une certification de cette nouvelle architecture.

La CI de réécriture ne publie **aucune archive intitulée mod final** : compilation, tests du cœur et vérification de l'adaptateur uniquement. Les validations numériques ne remplacent pas les scènes en jeu ni les budgets de performances sur un GPU réel.
