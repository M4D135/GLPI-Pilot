-- Benchmark: five fictitious tickets change in GLPI (new follow-up, new last modification date).
UPDATE glpi_tickets SET date_mod = '2026-10-28 15:03:00' WHERE id IN (3, 9, 15, 21, 27);
INSERT INTO glpi_itilfollowups (itemtype, items_id, date, users_id, content, is_private, requesttypes_id, date_mod, date_creation) VALUES
('Ticket', 3, '2026-10-28 15:03:00', 2, '&lt;p&gt;Suivi ajouté pour le banc.&lt;/p&gt;', 0, 1, NOW(), NOW()),
('Ticket', 9, '2026-10-28 15:03:00', 2, '&lt;p&gt;Suivi ajouté pour le banc.&lt;/p&gt;', 0, 1, NOW(), NOW()),
('Ticket', 15, '2026-10-28 15:03:00', 2, '&lt;p&gt;Suivi ajouté pour le banc.&lt;/p&gt;', 0, 1, NOW(), NOW()),
('Ticket', 21, '2026-10-28 15:03:00', 2, '&lt;p&gt;Suivi ajouté pour le banc.&lt;/p&gt;', 0, 1, NOW(), NOW()),
('Ticket', 27, '2026-10-28 15:03:00', 2, '&lt;p&gt;Suivi ajouté pour le banc.&lt;/p&gt;', 0, 1, NOW(), NOW());
