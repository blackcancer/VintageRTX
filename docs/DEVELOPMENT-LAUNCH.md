# Compilation, déploiement et lancement de la réécriture

La migration vers `VintageRTX.Client` avait conservé les références de compilation mais perdu le dossier Mods et les profils de lancement de recovery. Une DLL dans `bin/Debug/net10.0` n'est pas automatiquement découverte par le jeu. Ce correctif rétablit le contrat de développement de `dev/renderer-recovery-20260918` (référence `1a82a0ca`) sur la branche de réécriture, sans importer l'ancien renderer.

## Visual Studio

Ouvrir **VintageRTX.slnx** et définir **VintageRTX.Client** comme projet de démarrage, pas VintageRTX.Core ni l'ancien projet `src/VintageRTX/VintageRTX.csproj`. Les préférences utilisateur de Visual Studio peuvent encore désigner l'ancien projet ; elles ne sont pas écrasées par Git.

Les trois profils historiques sont rétablis, avec les mêmes noms, arguments, répertoire de travail et variables :

- **Vintage Story Client** : ouvre `foggy village world`.
- **Vintage Story Client (quick creative)** : ouvre `creative`.
- **Vintage Story Client (menu)** : reste au menu, sans sélectionner de sauvegarde.

Choisir le profil souhaité, puis F5. L'exécutable lancé est celui du dossier `VintageStoryPath`. `--addModPath` désigne désormais le dossier Mods de **VintageRTX.Client**, et `--addOrigin` ses assets. Les profils Windows utilisent `Vintagestory.exe`, comme auparavant. Ils ne lancent pas un jeu copié dans bin et ne remplacent aucun fichier de l'installation.

Les variables `VINTAGERTX_AUTO_*` des deux anciens profils sont conservées pour compatibilité de configuration. Les anciens automatismes de capture/benchmark du renderer recovery ne sont pas réintroduits dans le nouveau code ; leur présence n'annonce pas qu'ils sont exécutés.

## Dossier produit

Un build Debug normal produit directement :

```text
src/VintageRTX.Client/bin/Debug/Mods/vintagertx/
├── VintageRTX.dll
├── VintageRTX.Core.dll
├── VintageRTX.pdb
├── VintageRTX.Core.pdb
├── modinfo.json
└── assets/
    └── vintagertx/...
```

Release utilise le même contrat sous `bin/Release/Mods/vintagertx`. Le dossier `net10.0` supplémentaire est supprimé pour **le client seulement**. Le cœur et les projets de test conservent leurs sorties SDK normales. La dépendance `VintageRTX.Core.dll` est copiée avec le mod ; les DLL du jeu, Newtonsoft.Json et OpenTK restent `Private=false` et ne sont pas redistribuées dans le paquet.

`dotnet build src/VintageRTX.Client/VintageRTX.Client.csproj -c Debug` suffit pour produire ce dossier. Un contrôle après compilation refuse un paquet dépourvu de l'une des deux DLL, de `modinfo.json` ou du catalogue d'émission. Les assets et le manifeste utilisent `Always` pour ne pas conserver un ancien contenu ayant le même horodatage.

Les builds redirigés avec `--artifacts-path` restent isolés et n'écrasent pas le paquet local éventuellement chargé par un jeu en cours. Les profils F5 visent les builds locaux normaux, pas cette sortie de qualification.

## Chemin du jeu

La propriété MSBuild explicite `VintageStoryPath` est prioritaire. Sinon, `VINTAGE_STORY` accepte le dossier contenant `VintagestoryAPI.dll`, ou un sous-dossier direct tel que `Lib` ou `Mods` dont le parent contient cette DLL, comme dans recovery. Références et lancement utilisent le même dossier résolu. Aucun chemin personnel absolu n'est versionné.

Pour les tests client exécutés hors du jeu, définir `VINTAGE_STORY` sur la **racine** : le chargeur isolé de tests lit directement l'environnement. La tolérance Lib/Mods restaurée concerne l'évaluation MSBuild et les profils de lancement.

## Démarrage normal et diagnostic

F5 passe explicitement `--addModPath`. Le raccourci ordinaire du jeu ne reprend pas les arguments de Visual Studio : pour ce mode, installer uniquement le dossier `vintagertx` produit dans le dossier Mods actif du jeu, ou ajouter explicitement ce chemin au lancement. Ce correctif ne modifie pas le dossier utilisateur, les sauvegardes, les mods activés ni les raccourcis. Éviter deux installations actives de `vintagertx` (recovery et rewrite) ; rien n'est supprimé automatiquement.

Le chargement de la réécriture est reconnaissable au message `[VintageRTX] Rewrite R02` et aux commandes `.vrtxrewrite`, `.vrtxemissions`, `.vrtxlightlab on`. L'image native du monde reste active à cette étape ; le laboratoire PBR est désactivé par défaut. Un mod chargé et une passe PBR raccordée au monde sont deux validations différentes.

## Contrôles automatisés

`tools/build/verify_development_layout.py` construit les vrais projets Debug et Release dans une copie temporaire au chemin Unicode avec espaces. Il vérifie les chemins MSBuild, les trois profils, les contenus du paquet, la dépendance du cœur, le remplacement d'assets à horodatage constant, le publish et l'isolation des sorties redirigées. Un petit probe .NET charge les DLL du paquet et les références officielles pour vérifier le type ModSystem et la sélection client/serveur, puis vérifie le refus d'un paquet auquel on retire le cœur.

Ces contrôles n'ouvrent aucun monde, ne lancent pas une session graphique Vintage Story et ne prétendent pas mesurer la couverture C# du renderer. Les campagnes C#/GLSL et le seuil de couverture 100 % restent séparés et inchangés. Les résultats sont ceux du workflow associé au commit exécuté, pas une réussite présumée dans ce document.

Références : profils et csproj de recovery ; template officiel `anegostudios/VSdotnetModTemplates`, `VSModTemplates/templates/VintageStoryMod/_ProjectName_/Properties/launchSettings.json` ; wiki officiel « Client startup parameters ».
