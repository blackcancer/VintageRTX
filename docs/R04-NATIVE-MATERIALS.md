# R04 — matériaux conducteurs dans le terrain natif

Version expérimentale : **0.3.2-dev.1**, cible **Vintage Story 1.22.7**. Base distante : `95d5798c1c0db84eb184fb3cc94295a79636d12d`, branche inchangée `dev/renderer-rewrite-20260921`.

## Livraison et statut

Ce lot est fourni comme sources, patch applicable à cette base et mod compilé. La session qui l'a préparé disposait de lectures GitHub, mais pas d'action d'écriture ni de réseau depuis son terminal : **aucun push ni résultat de CI GitHub de ce lot ne sont revendiqués**. Le commit de qualification indiqué dans les preuves est un snapshot Git **local**, et non la tête distante.

Les tests C# utilisent les DLL et shaders officiels 1.22.7 et des contextes OpenGL cachés. Le résultat est un raccordement exécutable au shader du terrain, pas une capture de partie jouée. Les mesures de couverture C# sont fournies séparément ; l'exigence 100 % lignes ET branches reste inchangée. Les suites Python/ModernGL ne sont pas annoncées comme exécutées par cette qualification locale.

## Périmètre effectivement raccordé

`chunkopaque` résout désormais un matériau de surface explicite avant d'appeler **le même `evaluateMaterialDirect` que le laboratoire**. Une surface conductrice utilise son Fresnel complexe RGB, sa distribution GGX et sa rugosité. Chaque direction d'une source finie garde son propre calcul de matériau et de visibilité.

Le porteur couleur est encodé une seule fois pour la chaîne native brouillard/composition. Le shader n'emploie ni la couleur déjà éclairée du framebuffer comme albédo, ni un spéculaire blanc teinté après coup. L'ancienne modulation artistique `applyReflectiveEffect` n'est pas appliquée une seconde fois sur un pixel déjà traité ; elle reste inchangée sur le chemin de repli natif.

**Cela raccorde les reflets directs des sources lumineuses. Cela ne produit pas encore une image réfléchie de l'environnement**, des reflets secondaires/hors écran, une GI ou une nouvelle optique de l'eau. La contribution de ciel conserve un porteur natif approché, pondéré par le Fresnel de vue pour un conducteur. Ce porteur n'est ni une intégration d'environnement GGX, ni une preuve de visibilité du ciel.

L'intégration reste limitée aux **blocs dont la surface entière est déclarée homogène**. Elle ne déduit pas un métal de la couleur d'un pixel, du nom de la texture ou du bit natif de réflexion. Les maillages multi-matériaux (cage/verre/flamme, bois/métal, etc.) attendent un fournisseur par primitive et ne doivent pas adopter cette déclaration globale.

Les programmes `entityanimated`, `standard` et `chunktopsoil` conservent ici leur raccordement diffus R03. Ils ne lisent pas le matériau du bloc derrière l'entité ou l'objet dessiné. Les mains, armures, lingots, mèches et objets au sol ne deviennent donc pas implicitement des conducteurs grâce à ce lot.

## Contrat d'assets

La définition est lue dans les **attributs finaux du bloc**, après patches natifs et résolution de variantes :

```json
"vintageRtxMaterial": {
  "kind": "conductor",
  "roughness": 0.32,
  "eta": [0.20, 0.92, 1.10],
  "k": [3.91, 2.45, 2.14]
}
```

`eta` et `k` sont les composantes RGB de l'indice optique complexe effectif, sans unité. Elles ne sont pas des valeurs sRGB à décoder. `roughness` est la rugosité perceptuelle, dont le carré est utilisé comme paramètre microfacette par le noyau existant. Les valeurs de cet exemple et des trois presets livrés sont des **approximations artistiques initiales**, pas des mesures spectrales ou SI certifiées.

Le parseur exige `eta` dans `(0,32]`, `k` dans `[0,32]`, trois composantes finies chacune, et une rugosité finie dans `[0,1]`. Champs inconnus, doublons, tableaux incorrects, JSON excessivement grand et valeurs invalides sont refusés avec une erreur contextualisée. Une définition invalide conserve le rendu natif de ce bloc et un avertissement borné ; elle ne transforme pas silencieusement le bloc en métal ou en diffuseur.

Deux autres déclarations sont disponibles :

```json
"vintageRtxMaterial": { "kind": "diffuse" }
```

```json
"vintageRtxMaterial": { "kind": "native" }
```

Absence de déclaration : comportement R03, et repli natif pour un bloc marqué réfléchissant. `native` : choix explicite de ne pas remplacer la surface. `diffuse` : albédo issu de l'atlas natif. Un conducteur de rugosité nulle reste natif pour le moment : un miroir idéal a une mesure delta et ne doit pas être remplacé par une rugosité arbitrairement non nulle.

### Patch fourni

`assets/vintagertx/patches/world-materials.json` utilise `addmerge`, côté client, sur `game:blocktypes/metal/metalblock.json`, chemin `/attributesByType`. Il couvre seulement :

- `metalblock-new-*-copper` ;
- `metalblock-new-*-gold` ;
- `metalblock-new-*-silver`.

Cela inclut leurs variantes de style déclarées dans cet asset, pas les autres alliages ni l'état corrodé. Le test emploie le vrai patcher `ModJsonPatchLoader`, vérifie la conservation des autres attributs, des textures et des flags, puis parse les définitions ajoutées.

Pour désactiver uniquement le remplacement du cuivre dans un pack chargé après VintageRTX, remplacer sa définition complète :

```json
[
  {
    "file": "game:blocktypes/metal/metalblock.json",
    "side": "client",
    "op": "replace",
    "path": "/attributesByType/metalblock-new-*-copper/vintageRtxMaterial",
    "value": { "kind": "native" }
  }
]
```

Utiliser `replace` ici, et non `addmerge`, évite de conserver les paramètres `eta`, `k` et `roughness` du conducteur dans une déclaration `native` qui ne les accepte pas. Redémarrer le client après modification d'un pack : ce lot n'ajoute pas de commande de rechargement à chaud des patches. Le catalogue d'émission et les quantités variables de bougies restent indépendants de ce contrat de surface.

## Publication régionale et coût

La surface appartient à la même révision locale que la cellule géométrique. Un lecteur conserve l'ancien snapshot immuable jusqu'à publication du suivant. La table matérielle est transférée avant que la nouvelle région soit annoncée prête ; un reset ou une erreur d'upload ne peut pas associer une ancienne table au nouveau monde.

Deux texels RGBA32F par cellule : `eta.xyz/type`, puis `k.xyz/roughness`. La table du voisinage actuel (27 régions de 512 cellules) occupe **442 368 octets, soit 432 Kio côté GPU**, plus le tableau CPU de même taille et le petit tampon de transfert. Une unité de texture supplémentaire est utilisée et restaurée à la fin de la liaison.

Le bit 16 du champ de métadonnées existant signale une déclaration. Les bits bas conservent le nombre de triangles. Un terrain sans déclaration n'effectue pas les deux lectures de la table de matériaux. La géométrie commune reste partagée ; modifier la rugosité ne reconstruit pas son BVH.

Une région modifiée sans changement de matériau conserve le transfert précédent de **8 224 octets**. Une modification matérielle locale transfère **24 608 octets** pour les cellules, le matériau et les tags de cette seule région. Les initialisations et resets restent des transferts plus larges. Une fluctuation ou extinction de source ne transfère pas ces données de surface. **Ces tailles sont vérifiées par tests ; aucun gain FPS n'est annoncé.**

## Qualification et limites de géométrie

Les nouvelles classes testent le parseur, l'identité et les révisions régionales, les transferts, les patches officiels et les pixels du vrai programme natif. Le test conducteur compare sa valeur RGB à la référence CPU, change volontairement le RGB de lumière précalculée, éteint la source et mesure l'absence de transfert géométrique supplémentaire. Il compare également six modes natifs de brillance avec et sans ce traitement artistique.

Les essais existants des quatre programmes du monde, des grandes coordonnées, du rechargement des shaders et de la restauration OpenGL sont conservés. Le workflow fourni exige désormais les nouveaux tests et le fichier `native-material-pixels.json`, en plus des preuves R03 ; son exécution GitHub reste à réaliser après application et push du patch.

Le fournisseur d'occultation statique n'est pas élargi artificiellement. Une cellule `Unsupported` peut porter une surface réellement rasterisée, mais ses trajets d'occultation demeurent non résolus lorsqu'ils traversent sa géométrie inconnue. Le métal raccordé ne rend donc pas les lanternes, feuillages, chandeliers ou objets animés transparents pour obtenir des ombres faciles.

Les cartes de normales/rugosité, matériaux par face, l'état d'oxydation physique, les entités conductrices, les ombres animées, reflets secondaires, réfraction et GI restent hors du lot. Les détails d'albédo d'une texture métallique ne sont pas arbitrairement multipliés dans le Fresnel conducteur : leurs variations de rugosité/relief demandent encore les cartes et l'association par surface.
