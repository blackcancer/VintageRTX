# R02 — portabilité des états OpenGL et échecs transmis

Base : `da301ca0e5e163936b962c34c70b58ecf2f9c087`, branche `dev/renderer-rewrite-20260921`. Aucun changement des shaders de transport, des tolérances numériques ni des profils de qualité dans ce lot.

## Ce que montrent les onze journaux

Sept journaux mentionnent explicitement `InvalidEnum`, soit après la comparaison d'une image, soit avant la passe suivante. `FullLabImageMatchesCpuTrianglesMaterialsAndFiniteSourceReference` a terminé sa comparaison avec une erreur RGB normalisée maximale de `3.184913566656178E-06`, puis échoue sur `GL.GetError`. Le test d'états étrangers signale `1.8949333480122732E-07` avant la même erreur. Ces valeurs ne prouvent pas que tout le renderer fonctionne ; elles situent ces deux échecs après la comparaison numérique.

Les quatre autres résultats sont distincts : le test de compatibilité reçoit un flag forward-compatible inattendu ; le test d'injection reçoit une assertion du témoin de pilote au lieu de son exception injectée ; le test de liaison ne produit aucune exception ; le laboratoire n'effectue qu'un dessin sur les deux attendus. Les deux derniers symptômes de cascade ne doivent pas être assimilés à des mesures de qualité visuelle.

## État polygonal : le profil est l'autorité

L'ancien `RewriteDrawState.Dispose` décidait d'utiliser `GL_FRONT` et `GL_BACK` dès que les deux cases de son tableau de modes différaient. Ce raisonnement n'établit pas que ces cibles sont permises : en profil Core, `glPolygonMode` accepte seulement `GL_FRONT_AND_BACK`. Une deuxième case non significative peut déclencher exactement le mauvais chemin.

`RewritePolygonModes` interprète maintenant le résultat selon `GL_CONTEXT_PROFILE_MASK` et `GL_CONTEXT_FLAGS` du contexte réellement courant. En Core ou en forward-compatible, il conserve le mode unique et n'utilise jamais des appels de face séparés. Les deux modes indépendants sont conservés en vraie compatibilité. La lecture utilise toujours un espace de deux entiers pour rester sûre vis-à-vis des pilotes qui renseignent les deux emplacements. Aucun nom de fabricant ne décide du comportement.

Les nouveaux tests passent explicitement une seconde case nulle ou différente au décodeur de production, même sur Mesa qui peut renvoyer deux valeurs égales. Un contrôle négatif vérifie que l'appel séparé de l'ancien chemin est réellement refusé par le contexte Core. Les modes Point, Line et Fill sont restaurés pendant deux passages successifs. L'exception dans le corps de la passe restaure aussi les modes de compatibilité.

## Publication après restauration

`DirectImagePass.Render` n'annonce plus `Ready` avant la destruction de son garde d'état. Une cinquième vérification intervient après la restauration et avant la publication des identités de surfaces/lumières. Une erreur à cette étape laisse la sortie indisponible. Le test d'injection couvre désormais les cinq étapes, puis vérifie la reprise sans assouplir les contrôles et sans laisser d'erreur OpenGL dans la file.

La correction n'efface pas les erreurs OpenGL pour les ignorer et ne remplace pas `InvalidEnum` par un succès. Elle retire les appels non autorisés et avance la frontière de validation jusqu'à la fin réelle de l'opération.

## Contextes de tests et liaison

`PortableGlContext` initialise GLFW, remet ses hints à leurs valeurs de base, fixe explicitement le flag forward-compatible à vrai ou faux, puis crée la fenêtre cachée. Les hints GLFW persistent entre les créations ; certains chemins OpenTK ne réécrivent que les flags vrais. Le test reproduit donc Core puis Compatibility dans le même processus. Il imprime version, fournisseur, renderer, masque et flags réellement obtenus. Ce helper appartient uniquement aux tests ; il ne recrée pas le contexte du jeu.

Le précédent test de liaison supposait qu'un dépassement annoncé du nombre de samplers provoquerait nécessairement son échec sur chaque pilote. Le journal fourni contredit cette hypothèse. Le nouveau témoin compile séparément deux stages valides puis les lie avec un varying de même nom mais de types incompatibles et réellement consommé. Le linker est celui de `DirectImagePass`, pas un faux pilote. La reprise lie ensuite les mêmes stages avec des types compatibles. Les messages séparent compilation vertex/fragment et liaison.

## Chargement du mod et image du monde

Le contrat F5/dossier Mods rétabli avant ce lot est conservé : voir `DEVELOPMENT-LAUNCH.md`. Ce correctif OpenGL ne l'annule pas et ne réimporte pas l'ancien renderer.

**Le renderer réécrit ne remplace toujours pas l'image du monde.** `DirectImagePass` possède des surfaces de laboratoire ; `DirectLightLabRenderer` dessine son panneau Ortho. La capture de surfaces du monde et la composition de leur illumination ne sont pas raccordées. `.vrtxlightlab on` active ce laboratoire, pas un mode RTX du monde. Une CI verte ou le chargement de la DLL ne doit pas être présenté comme la livraison de cette intégration.

## Validation

Les résultats appartiennent au commit réellement exécuté et seront lus dans ses workflows. Aucun résultat présumé ni pourcentage de couverture extrapolé n'est inscrit ici. Les onze noms de tests transmis restent présents ; les contrôles de portabilité s'ajoutent à eux. Le seuil lignes/branches 100 % reste inchangé. La validation sous Mesa n'est pas un nouvel essai sur le GPU de l'utilisateur.

Références primaires :
- https://wikis.khronos.org/opengl/GlPolygonMode
- https://www.glfw.org/docs/latest/window.html#window_hints
- https://github.com/opentk/opentk/blob/master/src/OpenTK.Windowing.Desktop/NativeWindow.cs
- https://registry.khronos.org/OpenGL-Refpages/gl4/html/glLinkProgram.xhtml
