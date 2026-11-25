# Homebrew formula for Kapeyn. Install with:
#   brew tap jvbalcita/kapeyn https://github.com/jvbalcita/kapeyn
#   brew install kapeyn
# Kapeyn is built on your Mac from source, so macOS shows no "unverified developer" warning.
# The release workflow updates url/sha256 below on every new version.
class Kapeyn < Formula
  desc "Keep your Mac awake, intelligently (menu bar app and kapeyn command)"
  homepage "https://github.com/jvbalcita/kapeyn"
  url "https://github.com/jvbalcita/kapeyn/archive/refs/tags/v2.3.1.tar.gz"
  sha256 "732786047e031b45291bdf363d3c777ca73b707b62f19dda9751392816ebc3bd"
  license "MIT"
  head "https://github.com/jvbalcita/kapeyn.git", branch: "main"

  depends_on macos: :monterey

  def install
    cd "mac" do
      system "bash", "build.sh"
      prefix.install "dist/Kapeyn.app"
    end
    # The app's own binary is also the "kapeyn" command.
    bin.install_symlink prefix/"Kapeyn.app/Contents/MacOS/Kapeyn" => "kapeyn"
  end

  def caveats
    <<~EOS
      Start Kapeyn (the coffee cup appears in the menu bar):
        kapeyn on

      To start it automatically when you log in, right-click the cup and choose
      Settings > Start at login.

      If Teams or Slack still show you as Away, turn on Settings > Also nudge the mouse
      and allow Kapeyn under System Settings > Privacy & Security > Accessibility.
      After each `brew upgrade`, remove the old Kapeyn entry there and allow it again.
    EOS
  end

  test do
    assert_match "kapeyn run", shell_output("#{bin}/kapeyn help")
  end
end
