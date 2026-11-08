namespace GlpiPilot.Core;

public static class FieldMaterialDefaults
{
    // Generic suggestions, not an assertion that these items exist or are compatible with a device.
    // Stable IDs let a future catalogue merge preserve local names and precise references.
    public static FieldMaterialCatalogue Create()
    {
        var catalogue = new FieldMaterialCatalogue();
        void Category(string id, string name) => catalogue.Categories.Add(new(id, name));
        void Item(string id, string category, string label, string note = "", string unit = "piece")
            => catalogue.References.Add(new(id, category, "", label, unit, note));
        Category("computers", "Ordinateurs et clients légers");
        Category("displays", "Écrans et supports");
        Category("docking", "Stations d’accueil et adaptateurs");
        Category("peripherals", "Claviers, souris et périphériques");
        Category("cables", "Câbles et connectique");
        Category("power", "Alimentation et protection électrique");
        Category("network", "Réseau et Wi-Fi");
        Category("printing", "Impression et numérisation");
        Category("consumables", "Consommables et étiquetage");
        Category("audio-video", "Audio, vidéo et visioconférence");
        Category("telephony", "Téléphonie");
        Category("mobility", "Tablettes et mobilité");
        Category("components", "Stockage, mémoire et composants");
        Category("readers", "Lecteurs, badges et code-barres");
        Category("tools", "Outillage et diagnostic");
        Category("accessories", "Accessoires et transport");
        Item("computer-laptop", "computers", "Ordinateur portable", "Préciser le modèle et le chargeur adapté.");
        Item("computer-desktop", "computers", "Unité centrale");
        Item("computer-thin-client", "computers", "Client léger");
        Item("display-generic", "displays", "Écran", "Préciser la taille, les entrées vidéo et les câbles.");
        Item("display-stand", "displays", "Support d’écran", "Vérifier la fixation et le poids admissible.");
        Item("dock-usbc", "docking", "Station d’accueil USB-C", "Vérifier la compatibilité du poste, de la vidéo et de l’alimentation.");
        Item("adapter-hdmi-dp", "docking", "Adaptateur vidéo", "Préciser les connecteurs et le sens de conversion.");
        Item("adapter-usb-ethernet", "docking", "Adaptateur USB vers Ethernet");
        Item("keyboard-usb", "peripherals", "Clavier USB", "Préciser la disposition du clavier.");
        Item("mouse-usb", "peripherals", "Souris USB");
        Item("keyboard-mouse-wireless", "peripherals", "Ensemble clavier et souris sans fil", "Prévoir le récepteur et les piles compatibles.");
        Item("cable-rj45", "cables", "Cordon Ethernet RJ45", "Préciser la longueur et la catégorie du câble.");
        Item("cable-displayport", "cables", "Câble DisplayPort");
        Item("cable-hdmi", "cables", "Câble HDMI");
        Item("cable-usbc", "cables", "Câble USB-C", "Vérifier les capacités de données, vidéo et puissance nécessaires.");
        Item("cable-usb-printer", "cables", "Câble USB pour imprimante", "Vérifier les connecteurs.");
        Item("cable-roll", "cables", "Câble au mètre", "Préciser le type et la longueur nécessaire.", "metre");
        Item("charger-laptop", "power", "Chargeur de portable", "Préciser la référence compatible ; ne pas supposer l’interchangeabilité.");
        Item("power-cord", "power", "Cordon secteur", "Préciser les connecteurs et respecter les règles du site.");
        Item("power-strip", "power", "Bloc multiprise", "Utilisation selon les règles électriques du site.");
        Item("ups", "power", "Onduleur", "Préciser la puissance et les connecteurs requis.");
        Item("network-switch", "network", "Commutateur réseau", "Préciser la référence autorisée et la configuration prévue.");
        Item("wifi-access-point", "network", "Point d’accès Wi-Fi", "Préciser la référence et l’alimentation.");
        Item("network-module", "network", "Module réseau", "Préciser la référence exacte et la compatibilité.");
        Item("printer", "printing", "Imprimante");
        Item("scanner", "printing", "Scanner");
        Item("toner", "consumables", "Toner ou cartouche", "Choisir une référence précise compatible avant le départ.");
        Item("labels", "consumables", "Étiquettes d’identification", "Préciser le format du lot.", "lot");
        Item("cable-ties", "consumables", "Attaches pour câbles", "Préciser le contenu du lot.", "lot");
        Item("cleaning-kit", "consumables", "Kit de nettoyage adapté au matériel");
        Item("headset", "audio-video", "Micro-casque");
        Item("webcam", "audio-video", "Webcam");
        Item("speakerphone", "audio-video", "Haut-parleur de conférence");
        Item("desk-phone", "telephony", "Téléphone de bureau");
        Item("smartphone", "telephony", "Téléphone mobile");
        Item("tablet", "mobility", "Tablette");
        Item("tablet-case", "mobility", "Protection de tablette", "Vérifier le modèle.");
        Item("storage-ssd", "components", "Disque SSD", "Préciser format, interface et capacité ; respecter les procédures de données.");
        Item("memory-module", "components", "Barrette mémoire", "Préciser génération, format, capacité et compatibilité.");
        Item("external-storage", "components", "Support de stockage externe", "Utiliser un support autorisé selon la politique de sécurité.");
        Item("smart-card-reader", "readers", "Lecteur de carte à puce");
        Item("barcode-reader", "readers", "Lecteur de code-barres");
        Item("badge-reader", "readers", "Lecteur de badge");
        Item("screwdriver-kit", "tools", "Jeu de tournevis");
        Item("ethernet-tester", "tools", "Testeur de liaison réseau");
        Item("esd-kit", "tools", "Protection antistatique");
        Item("diagnostic-media", "tools", "Support de diagnostic autorisé");
        Item("transport-case", "accessories", "Sacoche ou caisse de transport");
        Item("security-cable", "accessories", "Câble antivol", "Vérifier la fixation compatible.");
        FieldPreparationService.Validate(catalogue);
        return catalogue;
    }
}
