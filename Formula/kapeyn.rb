# Homebrew formula for Kapeyn. Install with:
#   brew install jvbalcita/kapeyn/kapeyn
# Kapeyn is built on your Mac from source, so macOS shows no "unverified developer" warning.
# This file is kept in github.com/jvbalcita/kapeyn. On every new version, the release workflow
# updates url/sha256 below and copies it to github.com/jvbalcita/homebrew-kapeyn, the tap brew reads.
class Kapeyn < Formula
  desc "Keep your Mac awake, intelligently (menu bar app and kapeyn command)"
  homepage "https://github.com/jvbalcita/kapeyn"
  url "https://github.com/jvbalcita/kapeyn/archive/refs/tags/v2.4.0.tar.gz"
  sha256 "abdbfb8da6b354ba9651db50b824eb7af6f03568a88d10f8a810a554987e38e6"
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
