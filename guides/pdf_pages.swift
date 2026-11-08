// Rend des pages d'un PDF en PNG pour la relecture : swift pdf_pages.swift fichier.pdf dossier [largeur] [pages, ex. 1,3,5-7]
import Foundation
import PDFKit
import AppKit
let a = CommandLine.arguments
guard a.count >= 3, let doc = PDFDocument(url: URL(fileURLWithPath: a[1])) else { print("usage: pdf_pages.swift f.pdf dossier [largeur] [pages]"); exit(2) }
let width = a.count > 3 ? CGFloat(Double(a[3]) ?? 900) : 900
var wanted = Set<Int>()
if a.count > 4 { for part in a[4].split(separator: ",") { let r = part.split(separator: "-").compactMap { Int($0) }; if r.count == 2 { for i in r[0]...r[1] { wanted.insert(i) } } else if let i = r.first { wanted.insert(i) } } }
try? FileManager.default.createDirectory(atPath: a[2], withIntermediateDirectories: true)
print("pages: \(doc.pageCount)")
for i in 0..<doc.pageCount where wanted.isEmpty || wanted.contains(i + 1) {
  guard let page = doc.page(at: i) else { continue }
  let box = page.bounds(for: .mediaBox); let scale = width / box.width
  let img = page.thumbnail(of: NSSize(width: box.width * scale, height: box.height * scale), for: .mediaBox)
  guard let tiff = img.tiffRepresentation, let rep = NSBitmapImageRep(data: tiff), let png = rep.representation(using: .png, properties: [:]) else { continue }
  try? png.write(to: URL(fileURLWithPath: String(format: "%@/page-%02d.png", a[2], i + 1)))
}
