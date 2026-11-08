// Recadrage exact d'une image (PNG ou JPEG) : swift crop_image.swift entrée.png sortie.png x y largeur hauteur
// Origine en haut à gauche, en pixels. Refuse un cadre qui sort de l'image (sips recadre au centre avec --cropOffset 0 0).
import Foundation
import ImageIO
import UniformTypeIdentifiers
let a = CommandLine.arguments
guard a.count == 7, let x = Int(a[3]), let y = Int(a[4]), let w = Int(a[5]), let h = Int(a[6]) else {
  print("usage : swift crop_image.swift entrée sortie x y largeur hauteur"); exit(2) }
guard let src = CGImageSourceCreateWithURL(URL(fileURLWithPath: a[1]) as CFURL, nil),
      let img = CGImageSourceCreateImageAtIndex(src, 0, nil) else { print("erreur : image illisible : \(a[1])"); exit(1) }
guard x >= 0, y >= 0, w > 0, h > 0, x + w <= img.width, y + h <= img.height else {
  print("erreur : cadre \(x),\(y) \(w)x\(h) hors de l'image \(img.width)x\(img.height)"); exit(1) }
guard let cropped = img.cropping(to: CGRect(x: x, y: y, width: w, height: h)) else { print("erreur : recadrage impossible"); exit(1) }
let type = a[2].lowercased().hasSuffix(".jpg") || a[2].lowercased().hasSuffix(".jpeg") ? UTType.jpeg : UTType.png
guard let dst = CGImageDestinationCreateWithURL(URL(fileURLWithPath: a[2]) as CFURL, type.identifier as CFString, 1, nil) else { print("erreur : écriture impossible : \(a[2])"); exit(1) }
CGImageDestinationAddImage(dst, cropped, nil)
guard CGImageDestinationFinalize(dst) else { print("erreur : écriture impossible : \(a[2])"); exit(1) }
print("\(a[2]) : \(w)x\(h) depuis \(x),\(y)")
