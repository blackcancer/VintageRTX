# Premier retour en jeu : corrections et limites

Base testée par l'utilisateur : `45bb1b6cecce6b304c82eef19cc18c73b61df568`.
Sources corrigées : `2765ef238d24fc6ad6c7d404faa6ab40685aec81`.
Branche : `dev/renderer-recovery-20260918`. Pas de fusion dans main.

Ce lot n'est pas une validation visuelle du jeu et ne ferme pas tous les défauts signalés.

## Changements effectifs

### Mise à jour des lampes posées

Une modification d'émetteur ne déclenche plus à elle seule un scan complet du terrain, des liquides, de la pluie et de l'occupation solaire. La file de modifications de blocs n'est plus vidée pour cette raison. Le consommateur traite les éditions de géométrie et actualise séparément la liste des sources et leurs masques de cage. Le GPU reçoit ces petits buffers dans la génération correspondante.

Le champ indirect se recalcule dans une tâche sur des copies détachées. Elle ne lit ni le monde ni OpenGL. Un résultat est rejeté si la génération ou la révision géométrique a changé. Cela sépare l'apparition de l'éclairage direct de la reconstruction indirecte. Le chargement initial et le recentrage de volume restent soumis à leurs scans; aucune latence chiffrée en jeu n'a été mesurée.

### Émetteurs portés et objets au sol

`EntityLightCollector` examine les entités chargées à proximité, même lorsqu'elles ne figurent pas dans la petite liste de lumières sélectionnée par le moteur. Un objet au sol est interrogé depuis son ItemStack réel avec GetLightHsv. Une émission nulle reste nulle : aucune torche éteinte n'est rallumée artificiellement.

`EmitterAppearance` centralise la résolution du catalogue, des dimensions et de la photométrie pour les blocs et les sources connues provenant d'entités. Le getter d'un joueur peut déjà regrouper ses mains : les mains ne sont pas ajoutées une seconde fois à cet agrégat.

Limites importantes : un agrégat lumineux par entité, huit sources sélectionnées au maximum dans le backend actuel, et association aux tableaux moteur encore spatiale faute d'identifiant propriétaire public dans ces tableaux. Les objets sans position moteur associée utilisent un centre approché, pas un socket animé exact. Il ne s'agit pas du registre multi-émetteurs définitif ni d'une géométrie animée d'occlusion complète.

### Entités réceptrices

Le shader opaque `entityanimated.fsh` encode maintenant son identité dynamique et sa luminance non éclairée même sans sidecar PBR. L'absence de fichier normal/roughness ne doit plus faire disparaître la classification nécessaire au rééclairage de cette voie. Les dessins de première personne conservant leur profondeur spéciale restent exclus. Les renderers personnalisés et les autres voies de rendu des objets ne sont pas certifiés par ce seul changement.

### Ombres et contours

- Suppression de l'exception qui imposait une visibilité égale à un pour les lampes proches de la caméra : ces sources tracent maintenant les obstacles du monde.
- Suppression du verrouillage des petites variations de position sur la position historique.
- Quadrature de source finie indépendante de l'indice de la lampe dans les tableaux.
- Limite de sources maintenue à huit dans les différents niveaux : les budgets d'échantillons peuvent varier sans faire disparaître des lampes à chaque transition de niveau. Cela peut coûter davantage sur les petits profils.
- Lecture exacte des positions, profondeurs et métadonnées de normales. Les identités compactées ne sont plus interpolées aux contours.
- Suppression de la dilatation du G-buffer à partir de seulement deux voisins.
- Reconstruction des masques d'ombre basse résolution guidée par la géométrie du récepteur. En l'absence de voisin compatible, ce pixel retrace la visibilité au lieu d'importer celle d'un objet voisin.

Les ombres des cages mobiles et des corps animés ne sont pas soudain devenues exactes : leur représentation géométrique reste un travail distinct. Ce lot corrige la visibilité des lumières mobiles vis-à-vis du monde déjà représenté. Aucun gain de FPS ni disparition universelle du scintillement n'est certifié.

### Eau

La branche au-dessus de l'eau vérifie de nouveau la visibilité après déplacement de la surface : caméra au-dessus, rayon descendant, profondeur d'interface valide et devant la profondeur opaque, avec preuve de face liquide lorsque ce buffer est disponible (exception explicite des contenants). Les copies de couleur depuis un pixel de rive voisin sont désactivées. Le second ajout arbitraire de ciel par-dessus un reflet déjà résolu est supprimé.

La branche au-dessus de l'eau ne sert plus de miroir sous l'interface. Le rendu immergé n'est pas remplacé ici par un modèle complet air/eau avec réflexion interne totale. Les rives partielles et les contenants doivent être retestés : retirer une reconstruction incorrecte peut rendre visible une limite du composite natif.

### Flou en mouvement

L'accumulation de couleur finale sans reprojection est temporairement suspendue côté runtime. Le shader garde son interface d'uniformes valide et l'historique distinct des ombres n'est pas supprimé. L'option d'accumulation conserve donc son effet sur les ombres mais ne réactive pas cet ancien mélange RGB. Cette mesure retire une source de traînées; ce n'est pas une implémentation de TAA ou de reprojection en mouvement. Du bruit ou de l'aliasing auparavant masqué peut redevenir visible.

## Défauts encore ouverts

| Retour | État honnête après ce lot |
| --- | --- |
| Lumières posées tardives | Cause de rescan systématique retirée; latence réelle à mesurer dans un monde déjà chargé. |
| Teinte bleutée sur l'émetteur | Photométrie des sources connues harmonisée; couleur de surface non certifiée, reconstruction d'albedo encore approximative. |
| Entités non éclairées | Classification corrigée pour le shader animé opaque; vérifier les différentes familles et renderers. |
| Torches/lampes lâchées | Collecte explicite des objets réellement lumineux ajoutée; extinction, portée et sélection restent applicables. |
| Eau traversant les blocs / dessous | Garde de visibilité terminale et exclusion de la branche aérienne depuis dessous; intégration réelle à retester. |
| Ombres qui scintillent selon le niveau | Plusieurs causes retirées; pas de certification temporelle globale. |
| Cuivre/orange devenant bleu | Non résolu. Ne pas corriger en ajoutant du rouge ou en supprimant arbitrairement le ciel. |
| Lanterne portée et ombres | Même définition photométrique pour la source reconnue, occultation du monde activée; pose précise/cage animée encore incomplètes. |
| Métaux incohérents | Non résolu. Les gains spéculaires hérités, l'albedo reconstruit et le mélange des sources de réflexion nécessitent leur propre lot BSDF. |
| Normal maps peu visibles | Métadonnées non interpolées et voie entité corrigées; ni génération, ni force, ni TBN globalement requalifiés. |
| Flou / détourages lumineux | Mélange RGB sans reprojection et dilatation retirés, reconstruction d'ombre corrigée; bloom/FXAA natifs et toutes les voies de reflets restent à examiner. |

## Preuves exécutées

Run de publication GitHub Actions **35404343755**, job **105790861675** :

- Compilation Release du projet `src/VintageRTX/VintageRTX.csproj` contre le client officiel 1.22.7 avec .NET 10.0.401 : réussite, zéro erreur, quatre avertissements.
- `tests/recovery` : 4 tests réussis, dont 256 rayons comparés à un oracle exhaustif de boîtes, cas parallèle sur géométrie fine, états de fin de parcours et liaison du shader complet.
- `tests/feedback` : 3 tests réussis. Deux exécutent les fonctions GLSL de production pour les métadonnées compactées et les dix cas de visibilité de l'eau; le troisième vérifie le raccordement au code et l'absence des anciens raccourcis.
- Backend de test : Mesa llvmpipe, LLVM 20.1.2. Ce ne sont ni des captures de Vintage Story, ni une mesure sur la carte graphique de l'utilisateur.

Les critères existants de réception visuelle n'ont pas été assouplis et l'ancienne suite complète de preflight n'est pas déclarée verte. Les nouvelles sources existent comme C#/GLSL ordinaires; aucun script de transformation n'est utilisé au build du mod ou au chargement du jeu.

## Comparaison à réaliser en jeu

Conserver la configuration et le monde de référence. Démarrer par Quality sans adaptation pour isoler les changements, puis comparer les profils dans la même scène. Faire le cycle poser/retirer/tenir/jeter pour une seule source allumée, face à un mur avec un obstacle fin. Comparer les êtres animés sans PBR personnalisé. Contrôler l'eau depuis les deux côtés avec un bloc opaque devant, puis les rives en dalles/escalier.

Pour les teintes encore ouvertes, conserver une capture finale et les diagnostics `normal`, `material` et `voxel` sur le même cuivre, éclairage fixe. Ne pas modifier saturation/exposition pour compenser : il faut distinguer couleur de matériau, réponse de normale et énergie lumineuse. Fournir le commit, le fichier de configuration, le GPU/pilote et les journaux avec les nouvelles observations.
