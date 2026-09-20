# Cave-interior : artefacts réels du 20 septembre 2026

## Portée exacte

Archive utilisateur analysée : `20260920-091101-cave-interior.zip`. Les lignes ci-dessous désignent son `Logs/client-main.log`. Les PNG ont été comparés après décodage RGB8, sans modification des images. Aucun journal complet, paramètre d'authentification, sauvegarde ni image utilisateur n'est publié dans ce commit.

Base de code inspectée : `aabd06d27e9d121bb766cfd09e64a2ad5ea50b2a`. L'archive ne contient pas de manifeste établissant le SHA Git local, son état dirty ou les empreintes de la DLL et des shaders exécutés : ne pas affirmer que le build local a été identifié par son commit.

**Correctif publié : `1642d496b6fd8ee593af0120a29d6dad30f65e75`. Il corrige uniquement le hook de capture d'albedo RGB rejeté par Harmony. Les trois échecs GPU/FPS/rebond du scénario ne sont pas clôturés. Aucun shader, seuil d'acceptation ou paramètre de qualité n'a été modifié dans ce correctif.**

## 1. L'assertion commune n'est pas la cause

`InGameRealCaseTests.RepresentativeScenarioCompletesSuccessfully` est une même méthode DynamicData utilisée pour toutes les lignes du catalogue. Elle compare à zéro le résultat de RuntimeHarness. Tout échec des validateurs converge sur cette assertion. Un message identique à cette ligne ne prouve pas des causes identiques entre scénarios.

Le résultat 1 appartient au validateur. Dans cette exécution, le code système -1 du jeu correspond à l'arrêt volontaire après achèvement du scénario, pas à un crash démontré.

## 2. Défaut supplémentaire effectivement corrigé

Lignes 327–331 : `Raw albedo capture disabled; compatibility materials retained`, avec une ArgumentException Harmony demandant de patcher la déclaration `Vintagestory.Client.NoObf.ShaderProgramBase.Use()` plutôt que son alias hérité. La pile remonte à `RawAlbedoCapture.EnsureHook`.

Le correctif canonicalise la cible du mapping de l'interface `IShaderProgram.Use` avec `AccessTools.GetDeclaredMember` avant déduplication et installation. Cette même identité est conservée pour le retrait du hook. Les véritables overrides sont préservés ; GetBaseDefinition ne serait pas un remplacement correct.

Le masque PBR « file-backed » à 99,9 % ne prouve pas que cette capture RGB était active. Les deux chemins sont distincts. L'avertissement du hook n'est pas classé dans les erreurs génériques actuelles du validateur, d'où son absence des trois lignes FAIL.

### Qualification réellement exécutée

Run `35503229664`, job `106058574395`, source `1642d496b6fd8ee593af0120a29d6dad30f65e75`, client officiel Linux Vintage Story 1.22.7, SDK .NET 10.0.401, Ubuntu/Mesa :

- Compilation : zéro erreur ; les deux avertissements préexistants de nullabilité et de référence XML du mod restent présents.
- Anciennes classes et nouvelle classe RawAlbedoHookTests via le filtre partagé : **103 exécutés, 103 réussis, aucun ignoré**.
- RenderLab ProductionShaderCompilesAndRendersHeadlessly : **1 exécuté, 1 réussi**, environ 80 secondes, critères inchangés.
- Les cinq nouveaux tests couvrent héritage/déduplication, override/masquage, implémentation explicite/entrée nulle, exécution/retrait de vrais hooks Harmony sur des fixtures et installation/retrait sur les classes du client officiel.

Le TRX original rapporte : `Official shader hook evidence: types=43, inherited=43, aliases=43, unique implemented hooks=1; no GL call or world launch.`

Les 43 classes officielles partagent ici une même déclaration héritée, patchée une seule fois. Le test installe et retire réellement le hook ; il n'appelle pas Use() sur un shader officiel sans contexte GL. Ce n'est donc pas encore une preuve de pixels RGB capturés dans un monde Windows.

Artefact original : `reported-regressions-1642d496b6fd8ee593af0120a29d6dad30f65e75`, ID `10603410774`.

## 3. Rupture du transport indirect dynamique au niveau performance — ouverte

Lignes 412–413 : passage au palier adaptatif performance après 3,56 ms GPU pour un budget de configuration de 2,00 ms. Cible 1920 × 1009 ; ombres 640 × 337.

Lignes 421–422 : caméra souterraine contrôlée et injection d'une lumière par IRenderAPI.AddPointLight. Lignes 445 et 453 : deux générations du volume, toutes deux avec **zéro bloc émissif**. Ligne 459 : une lumière dynamique est bien suivie. Lignes 499–540 : toutes les captures utilisent effective-tier=performance.

Le code de FilmicDisplayRenderer envoie `voxelBounceRayCount = 0` au palier 2. Son commentaire désigne le cache CPU directionnel comme remplacement. Mais VoxelScene construit ce cache avec ses lumières de blocs, pas avec la table dynamique sélectionnée par le renderer. La source injectée de ce scénario n'est donc pas transportée par ce repli.

La vue `debugView == 8` n'affiche que `voxelLighting.bounce * pointLightBounceStrength * 8`, pas le cache d'irradiance. Avec zéro rayon, cette contribution vaut zéro.

La réparation doit assurer un transport réel de la table lumineuse active à chaque niveau : cache alimenté/invalidation par ces sources ou passe GI réduite avec reconstruction profondeur/normale, par exemple. Déplacements, extinction, retrait, permutation des slots et changements de géométrie doivent être couverts. Réactiver des rayons uniquement pour les captures, ajouter une teinte chaude ou diminuer le seuil du validateur serait un faux correctif.

## 4. Captures de contribution contaminées par le final — ouvertes

Les cinq images suivantes sont strictement identiques sur leurs **1 937 280 pixels RGB8** :

- `20260920-111522062-reflection-vintagertx.png`
- `20260920-111522528-voxel-reflection-vintagertx.png`
- `20260920-111523444-voxel-bounce-vintagertx.png`
- `20260920-111525661-water-vintagertx.png`
- `20260920-111526094-wetness-vintagertx.png`

SHA-256 commun des pixels RGB décodés : `79fa71551993f0665c8b623ee6c27e4cdd665aa85fbcfbd2a5c3ba2ded969871`. Minimum de canal : 1 ; maximum : 19 sur 255.

FrameCaptureService conserve ces vues après le shader final du jeu ; seuls ReflectionSource et EntityMirror disposent du chemin diagnostic pré-final spécifique. Le résidu d'image dans les cinq fichiers ne constitue donc pas une contribution brute propre à chacun de ces phénomènes. « Rebonds visibles : 99,9 % » ne prouve pas un rebond ; ces résultats sont notamment compatibles avec une sortie de rebond nulle suivie de la composition finale.

Séparer les diagnostics physiques pré-final, avec domaine/encodage déclarés, des comparaisons visuelles finales A/B. Un contrôle négatif doit produire une contribution nulle sans exiger que l'image finale du jeu soit elle-même noire. Ce chemin de capture n'est pas modifié par le correctif du hook.

## 5. Budgets encore dépassés

Ligne 549, mesure A/B/A sur RTX 3070 :

| Mesure | Référence | Effet |
|---|---:|---:|
| FPS moyens | 60,0 | 59,6 |
| 1 % low | 57,6 | 46,6 |
| Jitter | 0,32 ms | 1,29 ms |
| Mesure GPU du mod | 0,00 ms | 3,51 ms |

Le seuil GPU du scénario est **3,00 ms** : dépassement de **0,51 ms, soit 17 %**. Ne pas confondre ce seuil avec le budget adaptatif de configuration de **2,00 ms**.

Le minimum FPS est strictement 60,0. La référence à 60,0 suggère un plafonnement possible, mais le réglage de synchronisation/plafonnement n'est pas fourni : **VSync non démontrée**. RuntimeDataSandbox copie les paramètres client sans normaliser ces réglages. Ajouter des métadonnées graphiques non secrètes par liste blanche, jamais le clientsettings.json complet qui peut contenir l'authentification.

D'après les FPS affichés et arrondis, le surcoût moyen est environ 0,112 ms et celui du temps d'image au 1 % low environ 4,10 ms. Ces valeurs transformées ne remplacent pas les distributions brutes.

Ligne 550 : temps CPU-wall moyens liquide **2,264 ms**, display **2,370 ms**, voxel **0,010 ms**. Les lignes 545–547 confirment un travail de simulation liquide pendant la fenêtre de mesure. Le volume conserve 219 voxels fluides : aucune suppression arbitraire de simulation hors champ n'est justifiée par l'absence d'eau visible. Examiner périmètre actif, mises à jour inchangées et transferts. Ne pas additionner les durées CPU-wall et GPU comme si elles étaient indépendantes.

Aucune amélioration des 3,51 ms GPU ou des 59,6 FPS n'est revendiquée pour le correctif du hook.

## 6. Conditions de clôture

Rendre d'abord les diagnostics de contribution fiables ; réparer ensuite le transport des émetteurs dynamiques au niveau performance ; mesurer enfin les budgets sur la même scène avec identité de DLL/shaders/profil/paramètres graphiques. Couvrir source chaude, source froide, extinction, paroi occultante, déplacement et permutation des sources par des contrôles positifs et négatifs.

Ne pas relancer indistinctement toutes les scènes pour qualifier uniquement le hook. Ne pas annoncer que les 103 tests hors monde ou le RenderLab synthétique clôturent cave-interior, ni attribuer les trois échecs de cette archive à tous les autres scénarios sur la seule base de leur assertion commune.
