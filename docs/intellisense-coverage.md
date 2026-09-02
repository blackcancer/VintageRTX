# Couverture documentaire IntelliSense

VintageRTX considère la documentation XML comme un contrat de maintenance, pas comme une mesure
limitée à l'API publique. L'analyseur Roslyn `VRTXDOC001` inspecte le code de production et les trois
projets de test pendant la compilation, puis échoue sur toute déclaration nommée non documentée.

## Dénominateur

Le dénominateur inclut, quelle que soit leur visibilité (`public`, `internal`, `protected` ou
`private`) :

- classes, structures, records, interfaces, énumérations et delegates ;
- constructeurs, destructeurs, méthodes, opérateurs et conversions ;
- propriétés et indexeurs ;
- événements et membres d'énumération ;
- constantes et champs exposés hors stockage privé.

Les champs privés mutables ne sont pas des contrats IntelliSense. Leurs invariants doivent être
expliqués sur la méthode ou la propriété qui possède leur transition. Le code explicitement généré
est exclu ; aucun chemin de production et aucune visibilité ne bénéficie d'une exclusion.

La métrique globale agrège quatre compilations indépendantes : `VintageRTX`, `VintageRTX.Test`,
`VintageRTX.SurfaceDynamics.Test` et `VintageRTX.RenderLab`. Chaque projet référence directement
l'analyseur afin que Visual Studio, MSBuild et la CI appliquent le même gate sans propriété optionnelle.

## Commandes de validation

```powershell
dotnet build .\VintageRTX.sln -c Release --no-restore
```

Le projet d'analyse est une référence obligatoire de chacun des quatre projets audités : la commande
standard de Visual Studio, de CI ou du terminal exécute donc le contrôle sans propriété d'activation.

Pour obtenir la métrique exacte, y compris le diagnostic informatif `VRTXDOC000`, produire le SARIF :

```powershell
$projects = @(
  '.\src\VintageRTX\VintageRTX.csproj',
  '.\tests\VintageRTX.Test\VintageRTX.Test.csproj',
  '.\tests\VintageRTX.SurfaceDynamics.Test\VintageRTX.SurfaceDynamics.Test.csproj',
  '.\tools\VintageRTX.RenderLab\VintageRTX.RenderLab.csproj'
)
$projects | ForEach-Object {
  dotnet build $_ -c Release --no-restore `
    -p:ErrorLog=obj\Release\intellisense-audit.sarif
}
```

Le chemin SARIF reste relatif à chaque projet. Les quatre rapports se trouvent donc dans leur propre
`obj/Release/intellisense-audit.sarif`, sans concurrence d'écriture entre les compilations.

La validation est complète uniquement lorsque les quatre compilations sont vertes, sans `VRTXDOC001`,
et que chaque `VRTXDOC000` annonce `total/total (100.00%)`. La somme des quatre numérateurs et
dénominateurs constitue la couverture globale. `GenerateDocumentationFile` reste actif partout pour
que Visual Studio produise les fichiers XML consommés par IntelliSense et signale aussi les défauts de
l'API publique standard.

## Invariants à préserver

- Le G-buffer et l'atlas terrain de Vintage Story sont empruntés : VintageRTX ne détruit jamais leurs
  handles OpenGL.
- Le passage final capture puis restaure l'état OpenGL qu'il modifie, y compris unités de texture,
  framebuffers, programme, VAO, viewport et capacités.
- Le shader, le renderer et RenderLab partagent une ABI stricte : unités de texture, formats,
  encodages de canaux, espaces de coordonnées et unités physiques doivent évoluer ensemble.
- Les coefficients d'absorption et de diffusion des liquides sont en inverse-blocs ; les distances
  optiques sont en blocs monde. Les identifiants 0 et 255 du LUT restent des profils neutres réservés.
- Les sidecars `_n`, `_r`, `_m` et `_e` restent visibles dans le catalogue partagé afin que leur mod
  auteur puisse les résoudre. Les gardes ciblées les excluent uniquement des expansions d'albédo ;
  un défaut de ce filtre est fatal, car il peut remplacer des textures sans rapport.
- Les métriques GPU utilisent des requêtes asynchrones et ne doivent jamais attendre activement le
  pilote. Le 1 % low dérive du percentile 99 des temps CPU du buffer borné.
- Toute modification de qualité adaptative invalide ou laisse reconverger l'historique temporel ;
  l'hystérésis évite les oscillations visibles et protège les 1 % low.

Les commentaires XML doivent décrire contrats, unités, bornes, propriété des ressources, effets de
bord et raisons de sécurité. Une reformulation du nom du membre n'est pas une documentation suffisante.
