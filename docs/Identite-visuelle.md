# Identité GLPI Pilot

Le 28 octobre 2026, David a demandé un logo et une icône pour GLPI Pilot, puis une famille cohérente avec Domenia v2, Pesto et FILPRO. Les jeux sont exclus. L'interface de référence reste Domenia v2 ; le look et les animations GLPI déjà approuvés sont conservés.

Le symbole propre à GLPI associe une fiche de ticket et une flèche de progression. Après l'accord de famille initial bleu/indigo, David a choisi explicitement un **fond orange pastel pour GLPI**. Le symbole blanc, l'accent turquoise et le léger relief sont conservés. Cette couleur propre à GLPI ne change pas les thèmes de l'application. Ce visuel original n'est pas le logo officiel du projet GLPI.

## Fichiers et raccordement

- `src/GlpiPilot/Assets/brand-master.png` : original carré produit avec l'outil intégré de création d'images, sans service appelé à l'exécution.
- `web/brand/glpi-pilot-icon.png` : version 512 px pour la marque dans la navigation et le favicon. Le texte GLPI Pilot reste un vrai texte accessible, adapté au thème.
- `web/brand/glpi-pilot-logo.png` : logo horizontal sur fond blanc, utilisé dans le README et disponible pour les documents.
- `src/GlpiPilot/Assets/app.ico` : neuf tailles Windows, 16, 20, 24, 32, 40, 48, 64, 128 et 256 px. La propriété `ApplicationIcon` l'incorpore dans Standard et Admin ; la fenêtre native utilise l'icône de son propre EXE.

`build-tools/prepare-branding.py` réalise uniquement la conversion du master en PNG/ICO. Il utilise Pillow 12.3.0 ; il est facultatif pour la maintenance graphique. Le build et la reconstruction DEV consomment les fichiers versionnés et ne requièrent ni Pillow, ni accès à un modèle. Aucune image distante n'est chargée par l'application.

## Prompts d'origine — outil intégré imagegen

Master, à partir de l'ébauche du même symbole :

> Turn this into a clean FULL-BLEED SQUARE app icon master. Keep this exact white ticket with turquoise folded corner and the white up/right arrow. Enlarge the blue-indigo background so it reaches EVERY edge and EVERY corner of the square canvas completely. No margins, NO rounded exterior silhouette, NO transparency, NO backdrop, NO scattered pixels, NO outside area. The entire square must be an opaque smooth royal-blue-to-indigo field. Keep the white symbol optically centered with about 18 percent padding; its shapes must remain the same. This is a flat square master that the native UI can frame, not a photo of an icon. No text or mockup.

Logo horizontal, avec le master comme référence :

> Use case logo-brand. Create a polished horizontal official lockup for the GLPI Pilot application, wide 3:1 composition on a completely plain white background. On the left, use this EXACT blue-indigo icon with the white folded ticket, turquoise corner and upward-right arrow, displayed as a small rounded square. To its right write the exact text 'GLPI Pilot' in a refined highly readable geometric sans-serif, dark navy #17243A, medium-bold weight, generous balanced spacing. No subtitle, slogan, decorative lines or other objects. Keep ample white padding around the complete lockup. Icon and text optically centered on the same baseline/midline, with high-quality professional typography. Preserve the reference symbol and colors, no redesign. Crisp high-resolution finished logo, no mockup, no shadows outside the icon.

## Retouche finale — fond orange pastel

Le master et le logo horizontal existants servent chacun de cible à l'outil intégré imagegen. Seul le fond du symbole devient orange pastel ; le canevas du logo horizontal reste blanc.

Master :

> Use case: precise-object-edit. Edit this exact GLPI Pilot application icon. Change ONLY the blue/indigo square background to a soft, warm PASTEL ORANGE gradient, light apricot #F7C49B at upper left to peach orange #F0AD78 at lower right. Keep the background opaque and full bleed square, with no border or rounded outside corners. Preserve exactly the existing white ticket, its turquoise folded corner, the white up-right curved arrow, their geometry, placement, scale and gentle relief/shadow. The openings and spaces around the white symbol must show the same new pastel orange background, with no blue remnants. Keep the white symbol crisp and readable. No text, no extra elements. Same square composition. Do not redesign the symbol.

Logo horizontal :

> Use case: precise-object-edit. Edit this exact horizontal GLPI Pilot logo. Change ONLY the blue/indigo background inside the rounded application icon on the LEFT to a soft warm PASTEL ORANGE gradient: light apricot #F7C49B at upper left to peach orange #F0AD78 at lower right. Preserve the white ticket, turquoise folded corner, white up-right curved arrow, geometry, position, scale and gentle relief/shadow. The openings through the symbol reveal pastel orange, no blue remnants. Keep the exact dark navy text 'GLPI Pilot', its typography, spacing and position unchanged. Keep the large surrounding canvas WHITE, same horizontal layout. No additional text or elements, do not redesign anything.
