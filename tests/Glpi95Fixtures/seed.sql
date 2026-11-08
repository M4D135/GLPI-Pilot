-- Fictitious GLPI 9.5 data for capturing real page structures. No real person, site or ticket.
UPDATE glpi_users SET list_limit = 10, language = 'fr_FR', date_format = 1 WHERE name = 'glpi';
INSERT INTO glpi_itilcategories (id, name, completename, entities_id, is_recursive, level, is_helpdeskvisible, is_incident, is_request, is_problem, is_change, date_mod, date_creation)
VALUES (1, 'Impression', 'Impression', 0, 1, 1, 1, 1, 1, 1, 1, NOW(), NOW()), (2, 'Réseau', 'Réseau', 0, 1, 1, 1, 1, 1, 1, 1, NOW(), NOW());
