// Texte lu dans une image (Vision, macOS) : swift ocr_image.swift image.png [image2.png…]
// Sert à repérer dans les captures des mots à proscrire (ex. « SIMULATION ») avant de les mettre dans un guide.
import Foundation
import Vision
import ImageIO
for path in CommandLine.arguments.dropFirst() {
  guard let src = CGImageSourceCreateWithURL(URL(fileURLWithPath: path) as CFURL, nil),
        let img = CGImageSourceCreateImageAtIndex(src, 0, nil) else { print("== \(path) : illisible"); continue }
  let req = VNRecognizeTextRequest(); req.recognitionLevel = .accurate; req.recognitionLanguages = ["fr-FR", "en-US"]
  try? VNImageRequestHandler(cgImage: img, options: [:]).perform([req])
  print("== \(path)")
  for obs in req.results ?? [] {
    guard let t = obs.topCandidates(1).first?.string else { continue }
    let b = obs.boundingBox
    print(String(format: "  y=%4d x=%4d  %@", Int((1 - b.maxY) * CGFloat(img.height)), Int(b.minX * CGFloat(img.width)), t))
  }
}
