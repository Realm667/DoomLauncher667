namespace DoomLauncher.WinUI.Services;

public sealed partial class UiLocalization
{
    private static void AddUxTranslations()
    {
        string[][] rows =
        [
            ["BackupScopeNotice", "Backups and temporary files are excluded. Files referenced outside this installation need a separate backup.", "Sicherungs- und Tempordner werden ausgeschlossen. Außerhalb dieser Installation referenzierte Dateien separat sichern.", "Les anciennes sauvegardes et fichiers temporaires sont exclus. Sauvegardez séparément les fichiers externes.", "Se excluyen las copias anteriores y los temporales. Guarda por separado los archivos externos."],
            ["ImportedOn", "Imported on", "Importiert am", "Importé le", "Importado el"],
            ["OpenFolder", "Open containing folder", "Enthaltenden Ordner öffnen", "Ouvrir le dossier", "Abrir la carpeta"],
            ["ApplyRemainingConflicts", "Apply to remaining conflicts", "Für weitere Konflikte übernehmen", "Appliquer aux conflits suivants", "Aplicar a los conflictos restantes"],
            ["ImportBatchTitle", "Import mods", "Mods importieren", "Importer des mods", "Importar mods"],
            ["ImportBatchPreparing", "Processing selected files…", "Ausgewählte Dateien werden verarbeitet…", "Traitement des fichiers sélectionnés…", "Procesando los archivos seleccionados…"],
            ["ImportBatchSummary", "New: {0} · Updated: {1} · Skipped: {2} · Failed: {3}", "Neu: {0} · Aktualisiert: {1} · Übersprungen: {2} · Fehlgeschlagen: {3}", "Nouveaux : {0} · Mis à jour : {1} · Ignorés : {2} · Échecs : {3}", "Nuevos: {0} · Actualizados: {1} · Omitidos: {2} · Fallidos: {3}"],
            ["ImportBatchCancelled", "Import cancelled. Completed entries have been retained.", "Import abgebrochen. Bereits importierte Einträge bleiben erhalten.", "Import annulé. Les entrées terminées sont conservées.", "Importación cancelada. Se conservan las entradas completadas."],
            ["StateRecovered", "Settings were loaded from the last valid backup because the settings file could not be read. Check storage access before continuing.", "Die Einstellungen wurden aus der letzten gültigen Sicherung geladen, da die Einstellungsdatei nicht lesbar war. Bitte den Speicherzugriff prüfen.", "Les paramètres ont été chargés depuis la dernière sauvegarde valide. Vérifiez l’accès au stockage.", "Se cargaron los ajustes de la última copia válida. Comprueba el acceso al almacenamiento."],
            ["SaveLaunchChoices", "Save source port & IWAD", "Sourceport und IWAD speichern", "Enregistrer le port et l’IWAD", "Guardar motor e IWAD"],
            ["LaunchChoicesSaved", "Launch definitions saved.", "Startzuordnungen gespeichert.", "Définitions de lancement enregistrées.", "Definiciones de inicio guardadas."],
            ["LaunchSetupHint", "Select the source port or IWAD above to assign launch definitions. Automatic uses your configured defaults.", "Oben Sourceport oder IWAD auswählen, um die Startzuordnung festzulegen. Automatisch verwendet die eingestellten Standardwerte.", "Sélectionnez le port ou l’IWAD ci-dessus. Automatique utilise vos valeurs par défaut.", "Selecciona el motor o IWAD de arriba. Automático utiliza los valores predeterminados."],
            ["MissingSourcePortAction", "No source port is assigned. Select a source port in the details pane, or configure one in Setup.", "Kein Sourceport zugeordnet. In der Detailansicht einen Sourceport auswählen oder unter Einrichtung definieren.", "Aucun port défini. Sélectionnez-en un dans les détails ou la configuration.", "No hay motor asignado. Selecciónalo en los detalles o configúralo en Configuración."],
            ["MissingIwadAction", "No IWAD is assigned. Select an IWAD in the details pane, or configure one in Setup.", "Keine IWAD zugeordnet. In der Detailansicht eine IWAD auswählen oder unter Einrichtung definieren.", "Aucun IWAD défini. Sélectionnez-en un dans les détails ou la configuration.", "No hay IWAD asignado. Selecciónalo en los detalles o configúralo en Configuración."],
            ["Maintenance", "Backup, restore & duplicates", "Sicherung, Wiederherstellung und Dubletten", "Sauvegarde, restauration et doublons", "Copias, restauración y duplicados"],
            ["CreateBackup", "Back up installation", "Installation sichern", "Sauvegarder l’installation", "Crear copia de la instalación"],
            ["IncludeModArchives", "Include mod archives (recommended for a complete backup)", "Modarchive einschließen (für vollständige Sicherung empfohlen)", "Inclure les archives de mods (recommandé)", "Incluir archivos de mods (recomendado)"],
            ["RestoreBackup", "Restore backup into an empty folder", "Sicherung in leeren Ordner wiederherstellen", "Restaurer dans un dossier vide", "Restaurar en una carpeta vacía"],
            ["BackupReady", "Backup created and verified.", "Sicherung erstellt und geprüft.", "Sauvegarde créée et vérifiée.", "Copia creada y verificada."],
            ["RestoreReady", "Backup restored and verified. Start DoomLauncher667.exe in the selected folder.", "Sicherung wiederhergestellt und geprüft. DoomLauncher667.exe im gewählten Ordner starten.", "Sauvegarde restaurée et vérifiée. Lancez DoomLauncher667.exe dans le dossier choisi.", "Copia restaurada y verificada. Inicia DoomLauncher667.exe en la carpeta elegida."],
            ["BackupWithoutMods", "This backup excludes mod archives. Copy Data/Mods from your existing installation before playing.", "Diese Sicherung enthält keine Modarchive. Vor dem Spielen Data/Mods aus der bestehenden Installation kopieren.", "Cette sauvegarde exclut les mods. Copiez Data/Mods avant de jouer.", "Esta copia no incluye mods. Copia Data/Mods antes de jugar."],
            ["FindDuplicates", "Find duplicate mod files", "Doppelte Moddateien finden", "Rechercher les mods en double", "Buscar archivos de mods duplicados"],
            ["NoDuplicates", "No byte-identical mod files found.", "Keine bytegleichen Moddateien gefunden.", "Aucun fichier de mod identique trouvé.", "No se encontraron archivos de mods idénticos."],
            ["DuplicateSummary", "{0} duplicate groups · {1} reclaimable bytes. Files are unchanged. Compare the paths before removing copies.", "{0} Dublettengruppen · {1} freigebbare Bytes. Dateien sind unverändert. Vor dem Entfernen die Pfade vergleichen.", "{0} groupes · {1} octets récupérables. Aucun fichier modifié. Comparez les chemins avant suppression.", "{0} grupos · {1} bytes recuperables. No se modificaron archivos. Compara las rutas antes de eliminar copias."],
            ["MaintenanceDescription", "Back up the portable installation, restore into an empty folder, or compare mod files by SHA-256. Existing data is never replaced by restore.", "Portable Installation sichern, in leeren Ordner wiederherstellen oder Moddateien per SHA-256 vergleichen. Die Wiederherstellung ersetzt keine vorhandenen Daten.", "Sauvegardez l’installation, restaurez dans un dossier vide ou comparez les mods par SHA-256.", "Crea una copia, restaura en una carpeta vacía o compara mods mediante SHA-256."],
            ["CloseGamesFirst", "Finish the running game and generation/import operations before maintaining the installation.", "Laufendes Spiel sowie Generierung und Import vor der Wartung beenden.", "Terminez le jeu et les opérations en cours avant la maintenance.", "Finaliza el juego y las operaciones en curso antes del mantenimiento."],
        ];
        var languages = new[] { "en-US", "de-DE", "fr-FR", "es-ES" };
        foreach (var row in rows)
            for (var index = 0; index < languages.Length; index++)
                ((IDictionary<string, string>)Translations[languages[index]])[row[0]] = row[index + 1];
    }
}
