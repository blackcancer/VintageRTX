# Contrat de réécriture — 21 septembre 2026

## Décision et séparation avec recovery

Nouvelle arborescence active, nouveaux projets et nouvelle CI. L'ancien renderer, ses assets/shaders automatiques, ses tests textuels d'implémentation et ses workflows de publication ne sont pas copiés dans cette branche. Leur histoire Git et la branche recovery restent accessibles. Pas de fusion vers main, pas de force-push de recovery. Le parent historique du nouveau départ est `1a82a0ca6f8a7566c19f56cfa7852312764db755`.

Les composants anciens réutilisables sont des candidats, non des dépendances obligatoires : photométrie avec provenance vérifiée, contrats des captures de profondeur et d'albedo, décodage des textures, UI et traductions. Chaque reprise exige une revue de ses dépendances et un test de comportement. Le renderer monolithique et le snapshot global ne sont pas repris.

## Invariants

1. Un seul propriétaire du rendu en jeu. Le module R00 n'en possède aucun : le jeu dessine normalement.
2. Données de surface indépendantes de la lumière : albedo RGB linéaire, normales géométriques et de shading distinctes, rugosité, indices optiques, émission et couverture alpha. Aucun albedo dérivé de pixels déjà éclairés.
3. Un `LightFrame` évalué une fois par trame ; les passes consomment le même état. L'animation d'intensité ne reconstruit pas la géométrie ni le BVH.
4. Identité de source = propriétaire/canal/incarnation dans une session et dimension. Ni indice de tableau, ni proximité, ni teinte. L'extinction est un état, pas un profil à réactiver.
5. Région CPU copiée/immuable, puis upload, puis acquittement de la même révision. Un travail obsolète ne peut écraser une édition récente. `Unknown`, `Building`, `CpuReady`, `Ready` restent distincts.
6. Une transaction locale ne bloque pas une autre région. Les sources, l'eau statique et la simulation de vagues ont des cycles de vie séparés.
7. Aucune lecture du monde ou OpenGL dans les workers de calcul. Les callbacks venant d'autres threads transmettent des valeurs ; l'adaptateur les traite sur le thread autorisé.
8. Pas de double application sRGB, exposition ou tonemapping. Les normales et métadonnées ne passent pas par le transfert des couleurs.
9. Pas de visibilité inventée en sortie d'une région inconnue. Le backend utilise un repli explicitement compatible ou signale l'absence de support.
10. Chaque passe annonce ses ressources et révisions ; les lectures/écritures de textures en conflit sont interdites. Les capacités réellement disponibles déterminent le backend, jamais le nom commercial du GPU.

## Pipeline cible — pas encore implémenté en jeu

Acquisition des maillages et paramètres -> scènes par région et instances -> visibilité primaire raster -> lumière directe par listes spatiales -> visibilité ray-tracée -> diffuse GI -> réflexion GGX et interfaces -> reconstruction temporelle par signal -> composition HDR -> présentation unique.

La géométrie doit provenir des meshes de rendu, y compris alpha testé et transformations, pas des boîtes de collision. Les instances animées disposent de leur géométrie/pose propre et d'attaches d'émetteurs. La représentation hybride peut accélérer les cubes pleins par grille mais ne remplace pas les silhouettes fines par ces cubes.

Les premières passes GPU visent OpenGL réellement exposé par 1.22.7. Un backend matériel spécifique est une extension après faisabilité et mesure d'interopérabilité, pas une promesse implicite de RT cores. Les modules GLSL restent séparés par fonction et partagent la même BSDF.

Les reflets ne se limitent pas au framebuffer principal : SSR est une accélération avec support connu, un rayon hors écran consulte la scène. Miroirs plans identifiés par surface/plan ; rough reflections échantillonnent une distribution et son PDF, pas un rayon décalé latéralement. L'eau a une interface utilisable avant la simulation de vagues, avec Fresnel, réfraction et absorption séparés. Aucune réflexion aérienne recopiée sous l'eau.

## Jalons

| Lot | Livrable | Réception exigée |
|---|---|---|
| R00 (ce commit) | Registre, profils temporels, transactions régionales, scheduler, intersections/BVH et référence BSDF/transport, observation API | Tests mathématiques et de publication, compilation adaptateur ; aucune réception visuelle revendiquée |
| R01 | Acquisition maillages/matières, publication GPU par région, lumière directe et ombres | Lampe initiale/posée/retirée sans attendre pluie/soleil/GI ; ombre d'une cage et de formes fines ; mesures de latence complète |
| R02 | Instances et attaches animées, plusieurs canaux/main, météo réelle | Pas de double compte des mains ; source suit l'attache ; locustes respectent l'état moteur ; foudre émet et s'éteint dans le même état de trame |
| R03 | Réflexions miroir/GGX hors écran et interfaces eau/verre | Identité du hit, profondeur et couleur cohérentes, aucune fuite à travers les silhouettes |
| R04 | GI multi-rebonds, historique/reprojection par signal | Lumière vacillante/éteinte sans traînée ; reference offline convergente ; pas de biais de teinte ou d'énergie inventée |
| R05 | Indexation spatiale GPU, compaction, budgets, menu natif et profils | Coûts CPU/GPU séparés, tests à froid/mouvement/charge ; options réellement actives et rollback |

R00 n'est pas présenté comme une optimisation graphique déjà visible. R01 est la première porte de comparaison en jeu. Ne pas réintroduire un drapeau global `IsSettled` pour réussir des captures. Un test de capture peut attendre ses dépendances déclarées ; le rendu normal ne doit pas attendre tous les producteurs.

## Mesures à conserver

Latence événement client -> observation -> révision -> upload -> première image contributive (p50/p95/p99), coût de chaque phase CPU, uploads/allocations/bytes, timing GPU de chaque passe et total, mémoire résidente, divergence par rapport à la référence, invalidations et rejets d'historique. Ne pas annoncer un gain FPS à partir d'un nombre de pixels ou d'un microbenchmark CPU.

Le budget de `WorkQueue` est vérifié entre unités de travail. Il ne peut interrompre un callback externe coûteux ; limiter et profiler les unités est obligatoire. Les objectifs de 1–2 frames après publication GPU ne dispensent pas de mesurer le temps passé avant publication.

## Sources techniques vérifiées

API client : https://apidocs.vintagestory.at/api/Vintagestory.API.Client.IClientEventAPI.html
Étapes de rendu : https://apidocs.vintagestory.at/api/Vintagestory.API.Client.EnumRenderStage.html
Accès dimension-aware : https://apidocs.vintagestory.at/api/Vintagestory.API.Common.IBlockAccessor.html
Modèles de réflexion et distributions : https://www.pbr-book.org/4ed/Reflection_Models/Conductor_BRDF et https://www.pbr-book.org/4ed/Reflection_Models/Roughness_Using_Microfacet_Theory

Le code mathématique de ce lot est nouvellement écrit. Le tracer de référence utilise actuellement un échantillonnage GGX NDF avec PDF correspondant, non VNDF, et un nombre borné de rebonds. Il sert à qualifier les futures optimisations, pas à prétendre avoir déjà un moteur temps réel complet.
